namespace VolumeTrayAppWPF.Services;

/// <summary>
/// Per-key "latest-pending-wins" payload scheduler.
///
/// For each <typeparamref name="TKey"/> at most one payload is in flight at a time; any payload scheduled while
/// a payload is running for the same key replaces the queued one (only the latest scheduled payload runs after
/// the in-flight one finishes), matching the slider-drag pattern where a flurry of intermediate values should
/// collapse into a single write of the final value.
///
/// After each payload completes the throttler waits <see cref="CooldownMs"/> before starting the next one for
/// that key, so writes can't happen faster than the configured rate-limit even when the hardware finishes them
/// quickly.
///
/// The throttler enforces ordering only - it has no opinion about what the payload does. A payload that needs
/// sequence atomicity against other operations on the same key should still hold the appropriate mutex itself.
/// </summary>
/// <remarks>
/// The payload receives a <see cref="ThrottlerContext"/> so it can:
/// (a) honour cancellation when the throttler is disposed or the caller's token is signalled,
/// (b) probe <see cref="ThrottlerContext.HasReplacement"/> to bail early during retry/dwell waits when a fresher
/// payload has already been queued for the same key (no point completing the current write if a newer one will
/// overwrite it immediately anyway).
/// </remarks>
public sealed class AsyncThrottler<TKey>(int cooldownMs, IEqualityComparer<TKey>? comparer = null) : IDisposable
    where TKey : notnull
{
    private readonly Dictionary<TKey, Slot> _slots = new(comparer ?? EqualityComparer<TKey>.Default);
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdownTokenSource = new();
    private int _cooldownMs = Math.Max(0, cooldownMs);
    private bool _disposed;

    /// <summary>
    /// Minimum interval between successive payload starts for the same key. Mid-flight changes apply on the next
    /// cooldown wait (the currently-running payload's wait isn't shortened or extended retroactively).
    /// </summary>
    public int CooldownMs
    {
        get => _cooldownMs;
        set => _cooldownMs = Math.Max(0, value);
    }

    /// <summary>
    /// Schedules <paramref name="payload"/> for <paramref name="key"/>. If a payload is currently running for the
    /// key, the new payload replaces any payload that was already queued (latest-pending-wins) - only this one
    /// will run after the current one finishes its cooldown. Returns a Task that completes when this scheduled
    /// payload eventually runs to completion or, if it gets replaced before ever running, when the replacement
    /// chain terminates.
    /// </summary>
    public Task RunAsync(TKey key, Func<ThrottlerContext, Task> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_disposed) return Task.CompletedTask;

        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Slot slot;
        bool startDriver = false;

        lock (_gate)
        {
            if (_disposed)
            {
                completionSource.TrySetResult();
                return completionSource.Task;
            }

            if (!_slots.TryGetValue(key, out slot!))
            {
                slot = new Slot();
                _slots[key] = slot;
            }

            // If a queued payload is already waiting for this slot, mark its TCS as "replaced and completed" so
            // callers awaiting the older payload don't block forever - and signal the running payload (if any)
            // that it should exit any in-progress dwell early.
            if (slot.NextPayload != null) slot.NextCompletionSource?.TrySetResult();

            slot.NextPayload = payload;
            slot.NextCancellationToken = cancellationToken;
            slot.NextCompletionSource = completionSource;
            slot.ReplacementSignal.HasReplacement = true;

            if (!slot.DriverRunning)
            {
                slot.DriverRunning = true;
                startDriver = true;
            }
        }

        if (startDriver)
            _ = DriveSlotAsync(key, slot);

        return completionSource.Task;
    }

    /// <summary>
    /// Drops any queued payload for <paramref name="key"/>. The currently-running payload (if any) is not
    /// cancelled - the throttler has no way to forcibly stop a payload mid-step, only to prevent further work
    /// for the key. Use when an entry is being detached / demoted and the queued work is no longer relevant.
    /// </summary>
    public void Drop(TKey key)
    {
        TaskCompletionSource? droppedCompletionSource;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out Slot? slot)) return;

            droppedCompletionSource = slot.NextCompletionSource;
            slot.NextPayload = null;
            slot.NextCancellationToken = CancellationToken.None;
            slot.NextCompletionSource = null;
        }
        droppedCompletionSource?.TrySetResult();
    }

    /// <summary>
    /// True if a payload is currently running OR queued for <paramref name="key"/>.
    /// </summary>
    public bool IsBusy(TKey key)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out Slot? slot)) return false;
            return slot.DriverRunning || slot.NextPayload != null;
        }
    }

    /// <summary>
    /// Waits until every per-key driver loop has exited. Doesn't dispose the throttler - call
    /// <see cref="Dispose"/> after a successful drain.
    /// </summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            int active;
            lock (_gate)
            {
                active = 0;
                foreach (Slot throttleSlot in _slots.Values)
                    if (throttleSlot.DriverRunning) active++;
            }
            if (active == 0) return;
            try { await Task.Delay(TimeConstants.DrainPollIntervalMs, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Tell every running payload to bail out at its next cancellation check.
        // The slot driver loops will release any remaining TCSes when they unwind.
        try { _shutdownTokenSource.Cancel(); } catch { /* best-effort during shutdown */ }

        lock (_gate)
        {
            foreach (Slot throttleSlot in _slots.Values)
            {
                throttleSlot.NextCompletionSource?.TrySetResult();
                throttleSlot.NextPayload = null;
                throttleSlot.NextCompletionSource = null;
            }
        }

        try { _shutdownTokenSource.Dispose(); } catch { /* best-effort */ }
    }

    private async Task DriveSlotAsync(TKey key, Slot slot)
    {
        // The driver loop pulls the latest queued payload, awaits it, applies the cooldown, and repeats until no
        // replacement was queued during the cooldown. Exiting the loop is the only way DriverRunning flips back
        // to false - a fresh RunAsync call after that point starts a brand-new driver loop.
        while (true)
        {
            Func<ThrottlerContext, Task>? payload;
            CancellationToken externalCancellationToken;
            TaskCompletionSource? completionSource;

            lock (_gate)
            {
                if (_disposed || slot.NextPayload == null)
                {
                    slot.DriverRunning = false;
                    return;
                }

                payload = slot.NextPayload;
                externalCancellationToken = slot.NextCancellationToken;
                completionSource = slot.NextCompletionSource;
                slot.NextPayload = null;
                slot.NextCancellationToken = CancellationToken.None;
                slot.NextCompletionSource = null;
                slot.ReplacementSignal.HasReplacement = false;
            }

            CancellationToken linkedToken = !externalCancellationToken.CanBeCanceled
                ? _shutdownTokenSource.Token
                : CancellationTokenSource.CreateLinkedTokenSource(
                    externalCancellationToken, _shutdownTokenSource.Token).Token;
            ThrottlerContext throttlerContext = new(slot.ReplacementSignal, linkedToken);

            try
            {
                await payload(throttlerContext).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected on dispose / external cancel */ }
            catch (Exception exception)
            {
                WPFLog.Log($"AsyncThrottler: payload for key '{key}' threw: {exception.Message}");
            }
            finally
            {
                completionSource?.TrySetResult();
            }

            if (_disposed)
            {
                lock (_gate) slot.DriverRunning = false;
                return;
            }

            int cooldown = _cooldownMs;
            if (cooldown > 0)
            {
                try
                {
                    await Task.Delay(cooldown, _shutdownTokenSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate) slot.DriverRunning = false;
                    return;
                }
            }
        }
    }

    private sealed class Slot
    {
        public Func<ThrottlerContext, Task>? NextPayload;
        public CancellationToken NextCancellationToken;
        public TaskCompletionSource? NextCompletionSource;
        public bool DriverRunning;
        // Shared with the live ThrottlerContext via reference so HasReplacement flips are visible
        // without exposing the Slot type publicly.
        public readonly ReplacementSignal ReplacementSignal = new();
    }
}

/// <summary>
/// Carrier for the volatile "a fresher payload is queued" flag.
/// Public so <see cref="ThrottlerContext"/> can expose it without exposing the throttler's internal Slot type.
/// </summary>
public sealed class ReplacementSignal
{
    private volatile bool _hasReplacement;
    public bool HasReplacement
    {
        get => _hasReplacement;
        internal set => _hasReplacement = value;
    }
}

/// <summary>
/// Hands the running payload the two signals it needs to be a well-behaved tenant of the throttler:
/// (a) a CancellationToken that fires on disposal or caller-supplied deadline,
/// (b) a HasReplacement flag indicating that a fresher payload has been queued for the same key - payloads
/// should bail out of long dwells when this flips so the queued payload isn't kept waiting.
/// Replaces the prior <c>IThrottlerContext</c> interface + private nested struct pair.
/// </summary>
public readonly struct ThrottlerContext
{
    private readonly ReplacementSignal _signal;

    internal ThrottlerContext(ReplacementSignal signal, CancellationToken cancellationToken)
    {
        _signal = signal;
        CancellationToken = cancellationToken;
    }

    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// True once a fresher payload has been queued for the same key.
    /// Payloads should bail out of long dwells when this flips so the queued payload isn't kept waiting.
    /// </summary>
    public bool HasReplacement => _signal != null && _signal.HasReplacement;
}
