using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.Utils;
using Timer = System.Threading.Timer;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Managed wrapper around a single audio session (one app's stream into a device).
/// Owns the COM proxies, subscribes to <see cref="IAudioSessionEvents"/> for live volume / state /
/// disconnect updates, and surfaces the bindable surface area to WPF via INotifyPropertyChanged.
/// Thread model: COM events arrive on a worker thread; the wrapper marshals every observable
/// state mutation onto the UI dispatcher captured at construction.
/// </summary>
internal sealed class AudioSession : INotifyPropertyChanged, IDisposable
{
    // Event-context GUID lives in Audio/Interop/AudioInterop.cs as AudioEventContext.Value -
    // identifies us as the originator when our own writes echo back through the
    // IAudioSessionEvents callbacks. Same fixed value shared with AudioDevice.

    // Hardcoded AppID for the system-sounds session so it groups with itself across endpoints.
    // Mirrors EarTrumpet's "System.SystemSoundsSession" sentinel.
    private const string SystemSoundsAppID = "System.SystemSoundsSession";

    // Apartment-state contract for the COM RCWs below:
    //  - _control / _control2 / _simpleVolume / _meter were activated from the parent
    //    AudioDevice's IMMDevice on the WPF UI-thread STA. The audioses.dll proxies register the
    //    free-threaded marshaler, so the sample-timer threadpool worker can read _meter and the
    //    volume-throttler worker can write _simpleVolume - both rely on the FTM. UI-thread is
    //    still the canonical home for any non-meter / non-volume call.
    private readonly IAudioSessionControl _control;
    private readonly IAudioSessionControl2 _control2;
    private readonly ISimpleAudioVolume _simpleVolume;
    private readonly IAudioMeterInformation _meter;
    private readonly EventBridge _events;
    private readonly Dispatcher _dispatcher;
    private readonly ProcessExitMonitor? _processExitMonitor;
    private readonly VolumeThrottle _volumeWrite;
    private readonly bool _watchingProcess;

    private float _volume;
    private bool _isMuted;
    private string _displayName;
    private AudioSessionState _state;
    private ImageSource? _icon;
    // Refcounted reference to the cached icon entry. Owned by this session; disposed on session
    // teardown or replaced (via ApplyIconHandle) when a re-resolution lands on a different bitmap.
    private AppIconResolver.IconHandle? _iconHandle;
    private bool _disposed;
    private bool _disconnected;
    private AudioSessionDisconnectReason? _lastDisconnectReason;

    // Step-counter peak-meter lerp. Shared with AudioDevice via the MeterLerp struct.
    private MeterLerp _meterLerp;

    // Stuck-meter watchdog. Mirrors the AudioDevice-level watchdog: each fresh (min, max) pair
    // re-arms a one-shot timer; after MeterStaleWatchdogMs of bit-exact same-value reads the
    // callback flips _meterIsLatched and subsequent same-value samples force the lerp to silence.
    // Per-session meters need this independently of the device-level one because each session's
    // IAudioMeterInformation is its own COM proxy and can latch on its own (typically when an app
    // pauses playback without releasing the session, or after a default-device switch leaves the
    // session pointed at a quiescent endpoint).
    private readonly Timer _stuckMeterWatchdog;
    private float _lastRawPeakMin;
    private float _lastRawPeakMax;
    private volatile bool _meterIsLatched;

    public uint ProcessID { get; }
    public bool IsSystemSounds { get; }
    public string SessionInstanceID { get; }

    /// <summary>
    /// Stable identity used by <see cref="AudioAppGroup"/> to collate sessions belonging to the same app
    /// (e.g. Discord's three child processes). System sounds have a hardcoded id; other sessions key on the
    /// lower-cased process image path. When the process can't be opened, falls back to a pid-prefixed id so
    /// each unresolvable session still gets its own slider rather than silently grouping with others.
    /// </summary>
    public string AppID { get; }

    /// <summary>
    /// Resolved app icon. Updates at runtime when the session reports a new icon path
    /// via <see cref="IAudioSessionEvents.OnIconPathChanged"/>; null means the resolver
    /// gave up and the UI should render its fallback glyph.
    /// </summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set { if (!ReferenceEquals(_icon, value)) { _icon = value; OnPropertyChanged(); } }
    }

    public string DisplayName
    {
        get => _displayName;
        private set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _volume) < 0.0005f) return;

            // Update the cached value + raise PropertyChanged synchronously so the slider stays
            // responsive on fast drags. The COM write is queued through the shared throttler with
            // latest-pending-wins semantics so a flurry of pixel-level changes collapses into one
            // SetMasterVolume call per cooldown - and per-session, since VolumeThrottle keys on
            // the session id so Discord's three child sessions don't block each other.
            _volume = clamped;
            OnPropertyChanged();

            _volumeWrite.Write(clamped, (v, ctx) => _simpleVolume.SetMasterVolume(v, ref ctx));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;

            try
            {
                Guid ctx = AudioEventContext.Value;
                _simpleVolume.SetMute(value, ref ctx);
            }
            catch { return; }

            _isMuted = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Smoothed min(L, R) peak. Drives the base meter bar (the level guaranteed in both
    /// channels). Updated every render tick from <see cref="OnRenderTick"/>; never set externally.
    /// </summary>
    public float PeakValueMin => _meterLerp.DisplayMin;

    /// <summary>
    /// Smoothed max(L, R) peak. Drives the stereo overlay bar that paints on top of the base bar
    /// and extends to the loudest channel. For mono streams this equals <see cref="PeakValueMin"/>.
    /// </summary>
    public float PeakValueMax => _meterLerp.DisplayMax;

    public AudioSessionState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    /// <summary>True once the session has been disconnected by the device (e.g. endpoint removed).</summary>
    public bool IsDisconnected => _disconnected;

    /// <summary>
    /// The disconnect reason reported by IAudioSessionEvents::OnSessionDisconnected, or null
    /// when the disconnect was synthesized internally (e.g. process-exit watcher). Consumers
    /// read this in the Disconnected handler to distinguish ExclusiveModeOverride - which means
    /// another app grabbed the endpoint - from a plain device-removal / format-change shutdown.
    /// </summary>
    internal AudioSessionDisconnectReason? LastDisconnectReason => _lastDisconnectReason;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the session reports itself disconnected; AudioDevice removes the session.</summary>
    internal event Action<AudioSession>? Disconnected;

    /// <summary>Raised when the session expires/state-changes so AudioDevice can re-evaluate visibility.</summary>
    internal event Action<AudioSession>? StateChanged;

    public AudioSession(
        IAudioSessionControl control,
        Dispatcher dispatcher,
        AsyncThrottler<string> volumeThrottler,
        ProcessExitMonitor? processExitMonitor = null)
    {
        _control = control;
        _control2 = (IAudioSessionControl2)control;
        _simpleVolume = (ISimpleAudioVolume)control;
        _meter = (IAudioMeterInformation)control;
        _dispatcher = dispatcher;
        _processExitMonitor = processExitMonitor;

        // PID + system-sounds determination happens once; both are immutable for the session's lifetime.
        _control2.GetProcessId(out uint pid);
        ProcessID = pid;
        IsSystemSounds = _control2.IsSystemSoundsSession() == 0; // S_OK = 0 means it IS the system-sounds session

        _control2.GetSessionInstanceIdentifier(out string sessionInstanceID);
        SessionInstanceID = sessionInstanceID ?? string.Empty;
        _volumeWrite = new VolumeThrottle(volumeThrottler, "session:" + SessionInstanceID);

        // AppID routes the session to its group so it has to be ready synchronously - new sessions
        // arriving while a name lookup is async would otherwise race the AppID-keyed bucket lookup
        // in AudioDevice.AddSession. GetProcessImagePath uses kernel32 directly (OpenProcess +
        // QueryFullProcessImageName); fast (<1ms) so it stays inline.
        // The slow parts - AppIconResolver and FileVersionInfo lookups - are deferred to a
        // background task so a session-migration burst (5-10 apps reconnecting after the default
        // device flips) doesn't pile hundreds of ms of icon-extraction work onto the UI thread.
        // ReadSessionDisplayName stays synchronous because it's a single COM read; only the
        // process-based fallback (FileVersionInfo, slow) is deferred.
        if (IsSystemSounds)
        {
            _displayName = "System Sounds";
            AppID = SystemSoundsAppID;
        }
        else
        {
            _displayName = ReadSessionDisplayName(_control);
            if (string.IsNullOrEmpty(_displayName)) _displayName = "Unknown";

            string? imagePath = ProcessHelper.GetProcessImagePath(pid);
            AppID = string.IsNullOrEmpty(imagePath) ? $"pid:{pid}" : imagePath.ToLowerInvariant();
        }

        // Initial volume / mute / state pulled synchronously so the first paint shows real values.
        _simpleVolume.GetMasterVolume(out _volume);
        _simpleVolume.GetMute(out _isMuted);
        _control.GetState(out _state);

        // Stuck-meter watchdog (see field comment). Disarmed until the first different peak pair lands.
        _stuckMeterWatchdog = new Timer(OnStuckMeterWatchdog, null, Timeout.Infinite, Timeout.Infinite);

        // Wire callback. The bridge holds a reference back to us for state updates.
        _events = new EventBridge(this);
        _control.RegisterAudioSessionNotification(_events);

        // Watch the owning process. Force-killed apps don't always trigger Expired promptly because
        // the audio engine notices on its own schedule; the OS-level handle signal fires within
        // microseconds of the process record going away, so this collapses the disconnect latency.
        // System sounds (pid 0) and unreachable processes are no-ops in ProcessExitMonitor.Watch.
        if (_processExitMonitor != null && pid != 0 && !IsSystemSounds)
        {
            _watchingProcess = _processExitMonitor.Watch(pid, OnProcessExited);
        }

        // Kick off the deferred icon + display-name resolution. Capture pid into the closure so we
        // don't read the field after a possible Dispose; ResolveAsyncMetadata guards every COM hop
        // against a torn-down session.
        ScheduleAsyncMetadata(pid);
    }

    /// <summary>
    /// Spawns the heavy metadata resolution (icon extraction + process FileVersionInfo) off the
    /// UI thread and dispatches the results back. Called from the constructor after the cheap
    /// fields are populated; running on the threadpool keeps a session-migration burst from
    /// stalling the dispatcher.
    /// Icon extraction is retried up to <see cref="AppSettings.IconRetryAttempts"/> times with
    /// a linear backoff (waits grow as 1x, 2x, 3x the user-configured interval) - the first
    /// resolution often loses the race with the shell icon cache when an app has just launched.
    /// Display-name resolution runs once: FileVersionInfo doesn't have the same transient
    /// failure mode and rerunning it on every retry would chew through process handles.
    /// </summary>
    private void ScheduleAsyncMetadata(uint pid)
    {
        bool isSystemSounds = IsSystemSounds;
        _ = Task.Run(async () =>
        {
            // Each resolution can throw if the session is torn down between scheduling and run -
            // the AppIconResolver / ProcessHelper calls already swallow most failures; outer catch
            // is the belt that catches anything (e.g. RCW released).
            AppIconResolver.IconHandle? icon = TryAcquireIcon(pid, isSystemSounds);
            string? resolvedName = null;

            // Display-name fallback only runs when the constructor's synchronous read landed on the
            // "Unknown" placeholder. Avoid FileVersionInfo for system sounds (pid 0 is unreachable
            // anyway) and for sessions that already named themselves.
            if (!isSystemSounds)
            {
                try
                {
                    // If the session was disposed while we were extracting the icon, release the
                    // handle here so it ages out instead of leaking.
                    if (_disposed || _disconnected) { icon?.Dispose(); return; }
                    if (string.Equals(_displayName, "Unknown", StringComparison.Ordinal))
                    {
                        resolvedName = ProcessHelper.GetDisplayNameForProcess(pid);
                    }
                }
                catch { /* leave resolvedName null */ }
            }

            DispatchMetadata(icon, resolvedName);

            // Retry pass for the icon only. System sounds never miss (it's a hardcoded resource
            // ordinal) and a successful first attempt skips the whole loop. Wait grows linearly:
            // attempt 2 waits 1*interval, attempt 3 waits 2*interval, attempt 4 waits 3*interval.
            if (icon != null || isSystemSounds) return;

            int interval = AppServices.Settings?.IconRetryIntervalMs ?? AppSettings.IconRetryIntervalMsDefault;
            int wait = 0;
            for (int attempt = 2; attempt <= AppSettings.IconRetryAttempts; attempt++)
            {
                wait += interval;
                try { await Task.Delay(wait).ConfigureAwait(false); }
                catch { return; }

                if (_disposed || _disconnected) return;

                AppIconResolver.IconHandle? retried = TryAcquireIcon(pid, isSystemSounds: false);
                if (retried == null) continue;

                DispatchMetadata(retried, null);
                return;
            }
        });
    }

    private AppIconResolver.IconHandle? TryAcquireIcon(uint pid, bool isSystemSounds)
    {
        if (_disposed || _disconnected) return null;
        try { return AppIconResolver.Acquire(_control, pid, isSystemSounds); }
        catch { return null; }
    }

    private void DispatchMetadata(AppIconResolver.IconHandle? handle, string? resolvedName)
    {
        if (handle == null && string.IsNullOrEmpty(resolvedName)) return;
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _disconnected)
                {
                    // Session torn down between extraction and dispatch - drop the acquired
                    // ref so it can age out of the LRU rather than leaking as a strong root.
                    handle?.Dispose();
                    return;
                }
                if (handle != null) ApplyIconHandle(handle);
                if (!string.IsNullOrEmpty(resolvedName)
                    && !string.Equals(resolvedName, "Unknown", StringComparison.Ordinal))
                {
                    DisplayName = resolvedName;
                }
            });
        }
        catch
        {
            // Dispatcher shut down - release the acquired ref directly.
            handle?.Dispose();
        }
    }

    // UI-thread only. Swaps in the new handle and disposes the old one. The ReferenceEquals
    // fast-path drops the freshly acquired handle when re-resolution lands on the same cached
    // bitmap, keeping refcount churn balanced and skipping a no-op PropertyChanged notification.
    private void ApplyIconHandle(AppIconResolver.IconHandle newHandle)
    {
        if (_iconHandle != null && ReferenceEquals(_iconHandle.Icon, newHandle.Icon))
        {
            newHandle.Dispose();
            return;
        }
        AppIconResolver.IconHandle? old = _iconHandle;
        _iconHandle = newHandle;
        Icon = newHandle.Icon;
        old?.Dispose();
    }

    // UI-thread only. Drops the current handle and clears Icon - used when re-resolution
    // explicitly failed (e.g. OnIconPathChanged with an unresolvable new path).
    private void ClearIconHandle()
    {
        AppIconResolver.IconHandle? old = _iconHandle;
        _iconHandle = null;
        Icon = null;
        old?.Dispose();
    }

    /// <summary>
    /// If the session was constructed with a fallback identity (process unreachable at the time -
    /// DisplayName "Unknown", AppID "pid:NNN"), retry resolution now that the session is going Active.
    /// AppID stays put because it's the group routing key; only DisplayName and Icon are refreshed.
    /// In the rare case where the process is still unreachable, leaves the values untouched.
    /// </summary>
    private void TryReresolveProcessMetadata()
    {
        if (_disposed || _disconnected || IsSystemSounds) return;

        bool stuckName = string.Equals(_displayName, "Unknown", StringComparison.Ordinal);
        bool stuckIcon = _icon == null;
        if (!stuckName && !stuckIcon) return;

        if (stuckName)
        {
            string sessionName = ReadSessionDisplayName(_control);
            string resolved = !string.IsNullOrEmpty(sessionName)
                ? sessionName
                : ProcessHelper.GetDisplayNameForProcess(ProcessID);
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, "Unknown", StringComparison.Ordinal))
            {
                DisplayName = resolved;
            }
        }

        if (stuckIcon)
        {
            AppIconResolver.IconHandle? acquired = AppIconResolver.Acquire(_control, ProcessID, isSystemSounds: false);
            if (acquired != null) ApplyIconHandle(acquired);
        }
    }

    private static string ReadSessionDisplayName(IAudioSessionControl control)
    {
        try
        {
            control.GetDisplayName(out string name);
            // Apps occasionally hand back a "@path,resource" indirect string. Treat those as no-name
            // and fall through to the process-based resolver instead of surfacing the raw indirect.
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            if (name.StartsWith('@')) return string.Empty;
            return name;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Process-exit callback fired by <see cref="ProcessExitMonitor"/> on its watcher thread.
    /// Marshals to the dispatcher and raises Disconnected so the owning AudioDevice removes the
    /// session immediately - faster than waiting for the audio engine's eventual Expired notification.
    /// </summary>
    private void OnProcessExited()
    {
        try
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _disconnected) return;
                _disconnected = true;
                Disconnected?.Invoke(this);
            });
        }
        catch
        {
            // Dispatcher could be shutting down; nothing to do.
        }
    }

    /// <summary>
    /// Bg-thread half of the sample tick. Reads the per-channel peaks via COM and stashes them on
    /// the <see cref="MeterLerp"/> via <see cref="MeterLerp.WriteRawPeaks"/> off the UI thread.
    /// Float writes are atomic in .NET and the subsequent UI <see cref="OnNewSample"/> is queued
    /// via Dispatcher.BeginInvoke, which provides the release/acquire fence the UI thread needs.
    /// The (unified, biasMultiplier) pair flows into <see cref="MeterReader.ReadStereoPeaks"/>
    /// so the per-session lerp targets land already collapsed when unified mode is on.
    /// </summary>
    internal void UpdatePeakValueBackground(bool unified, int biasMultiplier)
    {
        if (_disposed || _disconnected) return;

        try
        {
            MeterReader.ReadStereoPeaks(_meter, unified, biasMultiplier, out float minPeak, out float maxPeak);

            if (minPeak != _lastRawPeakMin || maxPeak != _lastRawPeakMax)
            {
                // Fresh value - clear any latched-stale state, cache, write through, re-arm.
                _lastRawPeakMin = minPeak;
                _lastRawPeakMax = maxPeak;
                _meterIsLatched = false;
                _meterLerp.WriteRawPeaks(minPeak, maxPeak);
                _stuckMeterWatchdog.Change(TimeConstants.MeterStaleWatchdogMs, Timeout.Infinite);
            }
            else if (_meterIsLatched)
            {
                // Watchdog confirmed the meter is stuck - force silence so the lerp decays. Stays
                // in this branch until the COM call finally produces a genuinely different pair.
                _meterLerp.PinRawPeaksToSilence();
            }
            else
            {
                _meterLerp.WriteRawPeaks(minPeak, maxPeak);
            }
        }
        catch
        {
            // Meter can fail mid-disconnect; ignore until the disconnect callback fires.
            // Leave the previous raw values in place so the next successful sample reconciles.
        }
    }

    // Stuck-meter watchdog callback. See AudioDevice.OnStuckMeterWatchdog for the rationale.
    private void OnStuckMeterWatchdog(object? _)
    {
        if (_disposed || _disconnected) return;
        _meterIsLatched = true;
    }

    /// <summary>
    /// UI-thread half of the sample tick. Hands off to <see cref="MeterLerp.OnNewSample"/> which
    /// snapshots the current display values as the new lerp origins and arms the shared step
    /// counter at the supplied span.
    /// </summary>
    internal void OnNewSample(int interpolationSteps)
    {
        if (_disposed || _disconnected) return;
        _meterLerp.OnNewSample(interpolationSteps);
    }

    /// <summary>
    /// Render-timer callback. Advances the lerp and fires PropertyChanged on every actual change
    /// so the AudioAppGroup aggregator and the bound slider both redraw smoothly. UI-thread.
    /// </summary>
    internal void OnRenderTick()
    {
        if (_disposed || _disconnected) return;

        _meterLerp.OnRenderTick(out bool minChanged, out bool maxChanged);
        if (minChanged) OnPropertyChanged(nameof(PeakValueMin));
        if (maxChanged) OnPropertyChanged(nameof(PeakValueMax));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop watching the process before releasing COM proxies. If the watcher fires concurrently
        // the marshaled callback's _disposed guard collapses it to a no-op.
        if (_watchingProcess && _processExitMonitor != null)
        {
            try { _processExitMonitor.Unwatch(ProcessID); } catch { }
        }

        // Drop any queued SetMasterVolume so the throttler driver doesn't try to call into the
        // RCW we're about to release. A payload already in flight will catch the COM exception.
        _volumeWrite.Drop();

        // Disarm the stuck-meter watchdog. Timer.Dispose waits for an in-flight callback; the
        // _disposed early-return in OnStuckMeterWatchdog collapses one that slipped through.
        _stuckMeterWatchdog.Dispose();

        try { _control.UnregisterAudioSessionNotification(_events); }
        catch { /* session may already be gone */ }

        // Release the cached icon ref. With no more refs from any session, the entry parks in the
        // LRU "limbo" queue and stays revivable until evicted by overflow.
        Safe.Dispose(_iconHandle);
        _iconHandle = null;

        // The COM RCWs still hold native references; release them deterministically so the
        // session control's IUnknown ref count drops as soon as we abandon it.
        Safe.Release(_simpleVolume);
        Safe.Release(_meter);
        Safe.Release(_control2);
        Safe.Release(_control);
    }

    // Internal callback bridge. Lives on whatever MTA thread COM picks; every observable
    // mutation is dispatched onto the UI thread before raising PropertyChanged.
    private sealed class EventBridge : IAudioSessionEvents
    {
        private readonly AudioSession _owner;

        public EventBridge(AudioSession owner) { _owner = owner; }

        public int OnDisplayNameChanged(string newDisplayName, ref Guid eventContext)
        {
            string copy = newDisplayName;
            _owner._dispatcher.BeginInvoke(() =>
            {
                if (!string.IsNullOrEmpty(copy)) _owner.DisplayName = copy;
            });
            return 0;
        }

        public int OnIconPathChanged(string newIconPath, ref Guid eventContext)
        {
            // Apps publish an updated icon path (Discord on theme change, browsers on tab change, etc.).
            // Re-run the full resolution chain on the UI thread and swap the handle. A null result
            // wipes the icon, matching prior behavior where Resolve returning null cleared Icon.
            _owner._dispatcher.BeginInvoke(() =>
            {
                if (_owner._disposed || _owner._disconnected) return;
                AppIconResolver.IconHandle? newHandle = AppIconResolver.Acquire(
                    _owner._control, _owner.ProcessID, _owner.IsSystemSounds);
                if (newHandle != null) _owner.ApplyIconHandle(newHandle);
                else _owner.ClearIconHandle();
            });
            return 0;
        }

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            // Suppress echoes from our own writes.
            if (eventContext == AudioEventContext.Value) return 0;

            _owner._dispatcher.BeginInvoke(() =>
            {
                if (Math.Abs(newVolume - _owner._volume) >= 0.0005f)
                {
                    _owner._volume = newVolume;
                    _owner.OnPropertyChanged(nameof(Volume));
                }
                if (newMute != _owner._isMuted)
                {
                    _owner._isMuted = newMute;
                    _owner.OnPropertyChanged(nameof(IsMuted));
                }
            });
            return 0;
        }

        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, ref Guid eventContext) => 0;
        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => 0;

        public int OnStateChanged(AudioSessionState newState)
        {
            _owner._dispatcher.BeginInvoke(() =>
            {
                _owner.State = newState;
                if (newState == AudioSessionState.Active) _owner.TryReresolveProcessMetadata();
                _owner.StateChanged?.Invoke(_owner);
            });
            return 0;
        }

        public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        {
            _owner._dispatcher.BeginInvoke(() =>
            {
                _owner._disconnected = true;
                _owner._lastDisconnectReason = disconnectReason;
                _owner.Disconnected?.Invoke(_owner);
            });
            return 0;
        }
    }
}
