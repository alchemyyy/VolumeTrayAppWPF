using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Services;

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
    // The same fixed event-context GUID as AudioDevice; identifies us as the originator
    // when our own writes echo back through the IAudioSessionEvents callbacks.
    internal static Guid EventContext { get; } = new(AppIdentity.AppGuid);

    // Hardcoded AppId for the system-sounds session so it groups with itself across endpoints.
    // Mirrors EarTrumpet's "System.SystemSoundsSession" sentinel.
    private const string SystemSoundsAppId = "System.SystemSoundsSession";

    private readonly IAudioSessionControl _control;
    private readonly IAudioSessionControl2 _control2;
    private readonly ISimpleAudioVolume _simpleVolume;
    private readonly IAudioMeterInformation _meter;
    private readonly EventBridge _events;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncThrottler<string> _volumeThrottler;
    private readonly ProcessExitMonitor? _processExitMonitor;
    private readonly string _volumeThrottlerKey;
    private readonly bool _watchingProcess;

    private float _volume;
    private bool _isMuted;
    private string _displayName;
    private AudioSessionState _state;
    private ImageSource? _icon;
    private bool _disposed;
    private bool _disconnected;

    // Step-counter linear-interpolation state for the stereo peak meter. Sample timer's bg-thread
    // half writes _rawPeakMin / _rawPeakMax from one IAudioMeterInformation.GetChannelsPeakValues
    // call - min/max over the first two channels. OnNewSample copies them into _target* and
    // snapshots the current display values as _prev*. The render timer lerps both toward their
    // targets together across _interpolationSteps frames; min and max share the step counter so
    // they advance in lockstep.
    private float _rawPeakMin, _rawPeakMax;
    private float _displayPeakMin, _displayPeakMax;
    private float _prevPeakMin, _prevPeakMax;
    private float _targetPeakMin, _targetPeakMax;
    private int _interpolationStep;
    private int _interpolationSteps = 1;

    public uint ProcessId { get; }
    public bool IsSystemSounds { get; }
    public string SessionInstanceId { get; }

    /// <summary>
    /// Stable identity used by <see cref="AudioAppGroup"/> to collate sessions belonging to the same app
    /// (e.g. Discord's three child processes). System sounds have a hardcoded id; other sessions key on the
    /// lower-cased process image path. When the process can't be opened, falls back to a pid-prefixed id so
    /// each unresolvable session still gets its own slider rather than silently grouping with others.
    /// </summary>
    public string AppId { get; }

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
            // SetMasterVolume call per cooldown - and per-session, since the throttler keys on
            // _volumeThrottlerKey so Discord's three child sessions don't block each other.
            _volume = clamped;
            OnPropertyChanged();

            float captured = clamped;
            _ = _volumeThrottler.RunAsync(_volumeThrottlerKey, _ =>
            {
                try
                {
                    Guid ctx = EventContext;
                    _simpleVolume.SetMasterVolume(captured, ref ctx);
                }
                catch
                {
                    // Session may have expired between the user's drag and the deferred write.
                    // OnSessionDisconnected / OnStateChanged will reconcile the UI shortly after.
                }
                return Task.CompletedTask;
            });
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
                Guid ctx = EventContext;
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
    public float PeakValueMin => _displayPeakMin;

    /// <summary>
    /// Smoothed max(L, R) peak. Drives the stereo overlay bar that paints on top of the base bar
    /// and extends to the loudest channel. For mono streams this equals <see cref="PeakValueMin"/>.
    /// </summary>
    public float PeakValueMax => _displayPeakMax;

    public AudioSessionState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    /// <summary>True once the session has been disconnected by the device (e.g. endpoint removed).</summary>
    public bool IsDisconnected => _disconnected;

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
        _volumeThrottler = volumeThrottler;
        _processExitMonitor = processExitMonitor;

        // PID + system-sounds determination happens once; both are immutable for the session's lifetime.
        _control2.GetProcessId(out uint pid);
        ProcessId = pid;
        IsSystemSounds = _control2.IsSystemSoundsSession() == 0; // S_OK = 0 means it IS the system-sounds session

        _control2.GetSessionInstanceIdentifier(out string sessionInstanceId);
        SessionInstanceId = sessionInstanceId ?? string.Empty;
        _volumeThrottlerKey = "session:" + SessionInstanceId;

        // AppId routes the session to its group so it has to be ready synchronously - new sessions
        // arriving while a name lookup is async would otherwise race the AppId-keyed bucket lookup
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
            AppId = SystemSoundsAppId;
        }
        else
        {
            _displayName = ReadSessionDisplayName(_control);
            if (string.IsNullOrEmpty(_displayName)) _displayName = "Unknown";

            string? imagePath = ProcessHelper.GetProcessImagePath(pid);
            AppId = string.IsNullOrEmpty(imagePath) ? $"pid:{pid}" : imagePath.ToLowerInvariant();
        }

        // Initial volume / mute / state pulled synchronously so the first paint shows real values.
        _simpleVolume.GetMasterVolume(out _volume);
        _simpleVolume.GetMute(out _isMuted);
        _control.GetState(out _state);

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
    /// </summary>
    private void ScheduleAsyncMetadata(uint pid)
    {
        bool isSystemSounds = IsSystemSounds;
        _ = Task.Run(() =>
        {
            // Each resolution can throw if the session is torn down between scheduling and run -
            // the AppIconResolver / ProcessHelper calls already swallow most failures; outer catch
            // is the belt that catches anything (e.g. RCW released).
            ImageSource? icon = null;
            string? resolvedName = null;

            try
            {
                if (_disposed || _disconnected) return;
                icon = AppIconResolver.Resolve(_control, pid, isSystemSounds);
            }
            catch { /* RCW gone; leave icon null */ }

            // Display-name fallback only runs when the constructor's synchronous read landed on the
            // "Unknown" placeholder. Avoid FileVersionInfo for system sounds (pid 0 is unreachable
            // anyway) and for sessions that already named themselves.
            if (!isSystemSounds)
            {
                try
                {
                    if (_disposed || _disconnected) return;
                    if (string.Equals(_displayName, "Unknown", StringComparison.Ordinal))
                    {
                        resolvedName = ProcessHelper.GetDisplayNameForProcess(pid);
                    }
                }
                catch { /* leave resolvedName null */ }
            }

            try
            {
                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || _disconnected) return;
                    if (icon != null) Icon = icon;
                    if (!string.IsNullOrEmpty(resolvedName)
                        && !string.Equals(resolvedName, "Unknown", StringComparison.Ordinal))
                    {
                        DisplayName = resolvedName;
                    }
                });
            }
            catch { /* dispatcher shut down */ }
        });
    }

    /// <summary>
    /// If the session was constructed with a fallback identity (process unreachable at the time -
    /// DisplayName "Unknown", AppId "pid:NNN"), retry resolution now that the session is going Active.
    /// AppId stays put because it's the group routing key; only DisplayName and Icon are refreshed.
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
                : ProcessHelper.GetDisplayNameForProcess(ProcessId);
            if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, "Unknown", StringComparison.Ordinal))
            {
                DisplayName = resolved;
            }
        }

        if (stuckIcon)
        {
            ImageSource? resolved = AppIconResolver.Resolve(_control, ProcessId, isSystemSounds: false);
            if (resolved != null) Icon = resolved;
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
    /// Bg-thread half of the sample tick. Reads the per-channel peaks via COM into
    /// <see cref="_rawPeakMin"/> / <see cref="_rawPeakMax"/> off the UI thread. Float writes are
    /// atomic in .NET and the subsequent UI <see cref="OnNewSample"/> is queued via
    /// Dispatcher.BeginInvoke, which provides the release/acquire fence needed for the UI thread
    /// to observe the new raw values.
    /// The (unified, biasMultiplier) pair flows into <see cref="MeterReader.ReadStereoPeaks"/>
    /// so the per-session lerp targets land already collapsed when unified mode is on.
    /// </summary>
    internal void UpdatePeakValueBackground(bool unified, int biasMultiplier)
    {
        if (_disposed || _disconnected) return;

        try
        {
            MeterReader.ReadStereoPeaks(_meter, unified, biasMultiplier, out float minPeak, out float maxPeak);
            _rawPeakMin = minPeak;
            _rawPeakMax = maxPeak;
        }
        catch
        {
            // Meter can fail mid-disconnect; ignore until the disconnect callback fires.
            // Leave the previous raw values in place so the next successful sample reconciles.
        }
    }

    /// <summary>
    /// UI-thread half of the sample tick. Snapshots the current display values as the new lerp
    /// origins, copies the most recent <see cref="_rawPeakMin"/> / <see cref="_rawPeakMax"/>
    /// (filled by <see cref="UpdatePeakValueBackground"/>) into <see cref="_targetPeakMin"/> /
    /// <see cref="_targetPeakMax"/>, and arms the shared step counter.
    /// </summary>
    internal void OnNewSample(int interpolationSteps)
    {
        if (_disposed || _disconnected) return;

        _prevPeakMin = _displayPeakMin;
        _prevPeakMax = _displayPeakMax;
        _targetPeakMin = _rawPeakMin;
        _targetPeakMax = _rawPeakMax;
        _interpolationStep = 0;
        _interpolationSteps = interpolationSteps < 1 ? 1 : interpolationSteps;
    }

    /// <summary>
    /// Render-timer callback. Advances the shared step counter and writes the lerped min/max
    /// peaks into <see cref="_displayPeakMin"/> / <see cref="_displayPeakMax"/>. Fires
    /// PropertyChanged on every actual change so the AudioAppGroup aggregator and the bound
    /// slider both redraw smoothly. UI-thread.
    /// </summary>
    internal void OnRenderTick()
    {
        if (_disposed || _disconnected) return;

        _interpolationStep++;

        float newMin, newMax;
        if (_interpolationStep >= _interpolationSteps)
        {
            newMin = _targetPeakMin;
            newMax = _targetPeakMax;
        }
        else
        {
            float t = (float)_interpolationStep / _interpolationSteps;
            newMin = _prevPeakMin + (_targetPeakMin - _prevPeakMin) * t;
            newMax = _prevPeakMax + (_targetPeakMax - _prevPeakMax) * t;
        }

        if (newMin != _displayPeakMin)
        {
            _displayPeakMin = newMin;
            OnPropertyChanged(nameof(PeakValueMin));
        }
        if (newMax != _displayPeakMax)
        {
            _displayPeakMax = newMax;
            OnPropertyChanged(nameof(PeakValueMax));
        }
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
            try { _processExitMonitor.Unwatch(ProcessId); } catch { }
        }

        // Drop any queued SetMasterVolume so the throttler driver doesn't try to call into the
        // RCW we're about to release. A payload already in flight will catch the COM exception.
        try { _volumeThrottler.Drop(_volumeThrottlerKey); } catch { }

        try { _control.UnregisterAudioSessionNotification(_events); }
        catch { /* session may already be gone */ }

        // The COM RCWs still hold native references; release them deterministically so the
        // session control's IUnknown ref count drops as soon as we abandon it.
        TryRelease(_simpleVolume);
        TryRelease(_meter);
        TryRelease(_control2);
        TryRelease(_control);
    }

    private static void TryRelease(object? rcw)
    {
        if (rcw == null) return;
        try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(rcw); }
        catch { /* ignore */ }
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
            // Re-run the full resolution chain on the UI thread so AppIconResolver's session.GetIconPath()
            // call sees the new value, then assign through the property setter to raise PropertyChanged.
            _owner._dispatcher.BeginInvoke(() =>
            {
                if (_owner._disposed || _owner._disconnected) return;
                _owner.Icon = AppIconResolver.Resolve(_owner._control, _owner.ProcessId, _owner.IsSystemSounds);
            });
            return 0;
        }

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            // Suppress echoes from our own writes.
            if (eventContext == EventContext) return 0;

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
                _owner.Disconnected?.Invoke(_owner);
            });
            return 0;
        }
    }
}
