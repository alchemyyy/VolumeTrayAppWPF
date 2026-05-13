using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Plays the per-device / per-app volume-change feedback ding. Loads a single wav template at
/// construction, clones + PCM-scales it per app play, and routes through SoundPlayer / winmm.
/// Scroll-wheel callers use the trailing-edge dwell of <see cref="TimeConstants.VolumeFeedbackDingDelayMs"/>
/// so a burst of wheel ticks collapses into one ding; drag-release callers pass immediate=true
/// to fire on mouse-up without dwelling. Both paths share the throttler so an immediate play
/// preempts any in-progress wheel dwell on the same key.
/// </summary>
internal sealed class AppVolumeFeedbackPlayer : IDisposable
{
    // Default media file used for the per-app feedback ding. Picked because Windows Background.wav
    // is the same wav SystemSounds.Beep resolves to on default Windows installs, so the device
    // slider beep and the per-app slider beep sound consistent. Falls back to silence if missing.
    private const string AppFeedbackWavName = "Windows Background.wav";

    // Two throttler keys keep device and per-app feedback independent. Device keys are further
    // suffixed with the device id so two render endpoints can both ding without one preempting the
    // other.
    private const string DeviceDingThrottleKey = "device";
    private const string AppDingThrottleKey = "app";

    // Slice for the dwell's HasReplacement poll. Smaller = ding fires closer to "exactly N ms
    // after the last event"; larger = fewer wakeups. 10ms is well below human perception.
    private const int DingDwellPollSliceMs = 10;

    // Grace tacked onto the wav's natural duration when stamping "ding in flight". Covers the
    // MeterLerp's decay tail after the last sample renders so the peak hasn't quite returned to
    // zero by the time the bypass-window check runs on a rapid follow-up gesture.
    private const int DingMeterBypassGraceMs = 250;

    // Trailing-edge debouncer for the volume-change ding. Each scroll/wheel/drag-end calls
    // RunAsync; the payload polls HasReplacement during its dwell and bails the moment a fresher
    // event lands, so the ding only fires once the dwell elapses with no new event arriving.
    // Cooldown is 0 because the dwell itself IS the rate-limit.
    private readonly AsyncThrottler<string> _feedbackThrottler = new(0, StringComparer.Ordinal);

    // wav template loaded once. Each play clones it, scales PCM samples in-place by the target
    // app's slider value, and hands the bytes to SoundPlayer. SoundPlayer routes through winmm.dll's
    // PlaySound directly, which - unlike WPF's MediaPlayer - doesn't depend on Windows Media Player
    // and so works on Windows N installs.
    private WavTemplate? _wavTemplate;

    // Per-device UTC timestamp: the moment after which the meter should no longer reflect our own
    // ding. PlayForDevice consults this to bypass the suppress-when-audio-playing check while a
    // previously fired ding is still draining - otherwise the ding's own peak would chain-suppress
    // every follow-up ding during a fast scroll. Concurrent because device payloads run on the
    // throttler's pool, one per device id in parallel.
    private readonly ConcurrentDictionary<string, DateTime> _dingActiveUntilUtc = new(StringComparer.Ordinal);

    // Held across plays so the byte[] backing the in-flight async PlaySound isn't GC'd mid-playback
    // and so a follow-up play disposes the prior player (which preempts its still-playing sound).
    private System.Media.SoundPlayer? _currentAppSound;

    private readonly Dispatcher _uiDispatcher;
    private readonly AppSettings? _settings;
    private bool _disposed;

    public AppVolumeFeedbackPlayer(Dispatcher uiDispatcher, AppSettings? settings)
    {
        _uiDispatcher = uiDispatcher;
        _settings = settings;
        EnsureAppFeedbackData();
    }

    /// <summary>
    /// Routes the volume-change ding through the specific render endpoint the user just adjusted
    /// so the sound comes out of that device rather than the system default. Capture endpoints are
    /// skipped outright - microphones don't render audio.
    /// immediate=true skips the trailing-edge dwell; the throttler still serialises, so an immediate
    /// play preempts a pending wheel-dwell via the standard replacement signal.
    /// </summary>
    public void PlayForDevice(AudioDevice device, bool immediate = false)
    {
        if (_settings?.PlayDeviceVolumeChangeSound != true) return;
        if (device.IsCaptureDevice) return;

        EnsureAppFeedbackData();
        WavTemplate? wav = _wavTemplate;
        if (wav == null) return;

        string throttleKey = DeviceDingThrottleKey + ":" + device.Id;
        byte[] wavBytes = wav.Bytes;
        int dingWindowMs = wav.DurationMs + DingMeterBypassGraceMs;
        _ = _feedbackThrottler.RunAsync(throttleKey, async ctx =>
        {
            if (!immediate)
            {
                if (!await DwellWithReplacementBailAsync(ctx, TimeConstants.VolumeFeedbackDingDelayMs).ConfigureAwait(false)) return;
            }
            else if (ctx.HasReplacement) return;
            // Suppress the ding when the device is already rendering audio so the beep doesn't step on
            // music / calls / games. Bypassed while a previously fired ding is still draining on this
            // device so a fast scroll doesn't chain-suppress on its own residual peak. PeakValueMax is
            // the smoothed display value, fine for a coarse "is anything playing" gate.
            if (_settings?.SuppressDeviceVolumeChangeSoundWhenAudioPlaying == true && device.PeakValueMax > 0f)
            {
                bool ownDingInFlight = _dingActiveUntilUtc.TryGetValue(device.Id, out DateTime until)
                                       && DateTime.UtcNow < until;
                if (!ownDingInFlight) return;
            }
            // Stamp the bypass window BEFORE PlayChangeFeedback returns so any payload that lands
            // between this assignment and the actual playback start still sees an in-flight ding.
            _dingActiveUntilUtc[device.Id] = DateTime.UtcNow.AddMilliseconds(dingWindowMs);
            try { device.PlayChangeFeedback(wavBytes); }
            catch { /* feedback is best-effort */ }
        });
    }

    /// <summary>
    /// Per-app slider feedback. Clones the wav template, scales PCM samples to the app's current
    /// volume, and plays through SoundPlayer on the UI dispatcher.
    /// immediate=true fires on the caller's tick (drag-release) instead of waiting out the trailing
    /// dwell that scroll-wheel callers rely on.
    /// </summary>
    public void PlayForApp(float scalarVolume, bool immediate = false)
    {
        if (_settings?.PlayAppVolumeChangeSound != true) return;

        EnsureAppFeedbackData();
        if (_wavTemplate == null) return;

        // Capture scalarVolume in the closure: latest-pending-wins on the throttler means the payload
        // that ultimately runs is the most recent one queued, so the played volume reflects the user's
        // latest position rather than whatever it was when the gesture started.
        _ = _feedbackThrottler.RunAsync(AppDingThrottleKey, async ctx =>
        {
            if (!immediate)
            {
                if (!await DwellWithReplacementBailAsync(ctx, TimeConstants.VolumeFeedbackDingDelayMs).ConfigureAwait(false)) return;
            }
            else if (ctx.HasReplacement) return;
            try { await _uiDispatcher.InvokeAsync(() => PlayAppFeedbackNow(scalarVolume)); }
            catch { /* dispatcher torn down */ }
        });
    }

    private void PlayAppFeedbackNow(float scalarVolume)
    {
        WavTemplate? template = _wavTemplate;
        if (template == null) return;

        try
        {
            byte[] scaled = template.CloneScaled(scalarVolume);

            MemoryStream stream = new(scaled, writable: false);
            System.Media.SoundPlayer player = new(stream);
            player.Play();

            _currentAppSound?.Dispose();
            _currentAppSound = player;
        }
        catch { /* feedback is best-effort */ }
    }

    // Waits up to <paramref name="totalMs"/> in poll-sized slices, returning false the moment a fresher
    // payload is queued for the same key OR cancellation is signalled. Returns true only when the full
    // dwell elapses without a replacement -- the caller treats that as "ok to fire the ding".
    private static async Task<bool> DwellWithReplacementBailAsync(ThrottlerContext ctx, int totalMs)
    {
        int waited = 0;
        while (waited < totalMs)
        {
            if (ctx.HasReplacement) return false;
            int slice = Math.Min(DingDwellPollSliceMs, totalMs - waited);
            try { await Task.Delay(slice, ctx.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            waited += slice;
        }
        return !ctx.HasReplacement;
    }

    private void EnsureAppFeedbackData()
    {
        if (_wavTemplate != null) return;

        string wavPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media", AppFeedbackWavName);
        _wavTemplate = WavTemplate.FromFile(wavPath);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose the throttler before the SoundPlayer so any in-flight dwell exits via its
        // shutdown token before the payload tries to dispatch a play onto a torn-down dispatcher.
        try { _feedbackThrottler.Dispose(); }
        catch { /* shutdown best-effort */ }

        if (_currentAppSound != null)
        {
            try { _currentAppSound.Stop(); _currentAppSound.Dispose(); }
            catch { /* shutdown best-effort */ }
            _currentAppSound = null;
        }
        _wavTemplate = null;
    }
}
