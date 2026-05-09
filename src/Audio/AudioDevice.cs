using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Services;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Managed wrapper around an output audio endpoint (IMMDevice + Render).
/// Owns the endpoint volume + meter, the session manager, and the live session list for this device.
/// Subscribes to <see cref="IAudioEndpointVolumeCallback"/> for endpoint-level volume / mute changes
/// and to <see cref="IAudioSessionNotification"/> for newly-created sessions.
/// </summary>
internal sealed class AudioDevice : INotifyPropertyChanged, IDisposable
{
    private static Guid EventContext { get; } = new(AppIdentity.AppGuid);

    private readonly IMMDevice _device;
    private readonly IAudioEndpointVolume _endpointVolume;
    private readonly IAudioMeterInformation _endpointMeter;
    private readonly IAudioSessionManager2 _sessionManager;
    private readonly EndpointVolumeBridge _volumeBridge;
    private readonly SessionNotificationBridge _sessionBridge;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncThrottler<string> _volumeThrottler;
    private readonly ProcessExitMonitor _processExitMonitor;
    private readonly string _volumeThrottlerKey;

    // Groups live in a public-readable observable collection; AudioDevice mutates it on the UI thread only.
    // One group per AppId; sessions belonging to the same app (Discord's child processes, Chromium tabs,
    // etc.) collate into a single group so the flyout shows one slider per app.
    private readonly ObservableCollection<AudioAppGroup> _groups = [];

    // Dedup index by SessionInstanceId. Same COM session can be delivered twice during the brief
    // window between RegisterSessionNotification and EnumerateExistingSessions in the ctor:
    // OnSessionCreated marshals to the dispatcher, the synchronous enumerate picks the same session
    // up, and without this guard both arrivals create independent AudioSession wrappers around one
    // COM object - double meter polls, double event handlers, two sliders for one stream.
    private readonly Dictionary<string, AudioSession> _sessionsByInstanceId = new(StringComparer.Ordinal);

    private string _friendlyName;
    private float _volume;
    private bool _isMuted;
    private bool _isDefault;
    private bool _disposed;

    // Step-counter linear-interpolation state for the stereo peak meter. The sample timer's
    // bg-thread half writes _rawPeakMin / _rawPeakMax from one IAudioMeterInformation call (min
    // and max over the first two channels). The dispatched UI half (OnNewSample) copies them into
    // the _target* fields, snapshots the current display values as _prev*, and resets the step
    // counter. The render timer's UI tick advances _interpolationStep and lerps both display
    // values across _interpolationSteps frames; min and max share the step counter so they
    // advance together. With Fps > SampleRate, the dispatcher updates the lerps multiple times
    // per sample interval - the screen at vsync catches a stepped sequence of intermediate values
    // rather than a snap-to-latest sequence, which is what gives the meter its smoothness.
    private float _rawPeakMin, _rawPeakMax;
    private float _displayPeakMin, _displayPeakMax;
    private float _prevPeakMin, _prevPeakMax;
    private float _targetPeakMin, _targetPeakMax;
    private int _interpolationStep;
    private int _interpolationSteps = 1;

    public string Id { get; }
    public ReadOnlyObservableCollection<AudioAppGroup> Groups { get; }

    public string FriendlyName
    {
        get => _friendlyName;
        private set { if (_friendlyName != value) { _friendlyName = value; OnPropertyChanged(); } }
    }

    public bool IsDefault
    {
        get => _isDefault;
        internal set { if (_isDefault != value) { _isDefault = value; OnPropertyChanged(); } }
    }

    // Process-wide IPolicyConfig client. The COM object is cheap and reusable so we cache it across
    // calls; lazy init keeps non-default-switching sessions from paying the CoCreateInstance cost.
    private static IPolicyConfig? _policyConfigClient;

    /// <summary>
    /// Promotes this endpoint to the system default. Mirrors mmsys.cpl's "Set as Default Device":
    /// both Console (system sounds) and Multimedia roles are switched, while the Communications role
    /// is left alone so the user's separate communication-device choice is preserved.
    /// </summary>
    internal void SetAsDefault()
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        try
        {
            _policyConfigClient ??= (IPolicyConfig)new PolicyConfigClientComObject();
            _policyConfigClient.SetDefaultEndpoint(Id, ERole.eConsole);
            _policyConfigClient.SetDefaultEndpoint(Id, ERole.eMultimedia);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.SetAsDefault({FriendlyName}): {ex.Message}");
        }
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
            // SetMasterVolumeLevelScalar call per cooldown.
            _volume = clamped;
            OnPropertyChanged();

            float captured = clamped;
            _ = _volumeThrottler.RunAsync(_volumeThrottlerKey, _ =>
            {
                try
                {
                    Guid ctx = EventContext;
                    _endpointVolume.SetMasterVolumeLevelScalar(captured, ref ctx);
                }
                catch
                {
                    // Endpoint may have been torn down between the user's drag and the deferred write.
                    // The next OnNotify event (or device-changed rebuild) will reconcile the cached value.
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
                _endpointVolume.SetMute(value, ref ctx);
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public AudioDevice(IMMDevice device, Dispatcher dispatcher, AsyncThrottler<string> volumeThrottler, ProcessExitMonitor processExitMonitor)
    {
        _device = device;
        _dispatcher = dispatcher;
        _volumeThrottler = volumeThrottler;
        _processExitMonitor = processExitMonitor;
        Groups = new ReadOnlyObservableCollection<AudioAppGroup>(_groups);

        device.GetId(out string id);
        Id = id ?? string.Empty;
        _volumeThrottlerKey = "endpoint:" + Id;

        _friendlyName = ResolveFriendlyName(device);

        // Activate the endpoint volume + meter + session manager. Each Activate yields an
        // IUnknown* the runtime hands us as `object`; the cast here triggers QueryInterface.
        device.Activate(typeof(IAudioEndpointVolume).GUID, ClsCtx.ALL, IntPtr.Zero, out object volObj);
        _endpointVolume = (IAudioEndpointVolume)volObj;

        device.Activate(typeof(IAudioMeterInformation).GUID, ClsCtx.ALL, IntPtr.Zero, out object meterObj);
        _endpointMeter = (IAudioMeterInformation)meterObj;

        device.Activate(typeof(IAudioSessionManager2).GUID, ClsCtx.ALL, IntPtr.Zero, out object mgrObj);
        _sessionManager = (IAudioSessionManager2)mgrObj;

        _endpointVolume.GetMasterVolumeLevelScalar(out _volume);
        _endpointVolume.GetMute(out _isMuted);

        _volumeBridge = new EndpointVolumeBridge(this);
        _endpointVolume.RegisterControlChangeNotify(_volumeBridge);

        _sessionBridge = new SessionNotificationBridge(this);
        _sessionManager.RegisterSessionNotification(_sessionBridge);

        // Initial enumeration. New sessions arrive via OnSessionCreated thereafter.
        EnumerateExistingSessions();
    }

    /// <summary>
    /// Bg-thread half of the sample tick. Reads the endpoint per-channel peaks via COM into
    /// <see cref="_rawPeakMin"/> / <see cref="_rawPeakMax"/> and cascades into every group so
    /// per-session raw peaks are filled in parallel - all off the UI thread. The groups list is
    /// snapshotted under try/catch since UI-thread mutations could otherwise tear the enumerator;
    /// a torn frame just means we miss one tick for the affected device.
    /// The (unified, biasMultiplier) pair is forwarded into <see cref="MeterReader.ReadStereoPeaks"/>
    /// so unified mode collapses the per-channel result before it lands in the lerp targets.
    /// </summary>
    internal void UpdatePeakValueBackground(bool unified, int biasMultiplier)
    {
        if (_disposed) return;

        try
        {
            MeterReader.ReadStereoPeaks(_endpointMeter, unified, biasMultiplier, out float minPeak, out float maxPeak);
            _rawPeakMin = minPeak;
            _rawPeakMax = maxPeak;
        }
        catch
        {
            // Endpoint disconnect race - leave previous raw values in place; next sample reconciles.
        }

        AudioAppGroup[] groups;
        try { groups = _groups.ToArray(); }
        catch { return; }

        for (int i = 0; i < groups.Length; i++)
        {
            try { groups[i].UpdatePeakValueBackground(unified, biasMultiplier); }
            catch { /* group / session may have died between callbacks */ }
        }
    }

    /// <summary>
    /// UI-thread half of the sample tick. Snapshots the current display values as the new lerp
    /// origins, copies the most recent <see cref="_rawPeakMin"/> / <see cref="_rawPeakMax"/>
    /// (filled by <see cref="UpdatePeakValueBackground"/>) into the target fields, and arms the
    /// step counter at the supplied span (typically Fps / SampleRate). Forwards the same call
    /// into every <see cref="AudioAppGroup"/> so per-app session sliders interpolate too.
    /// </summary>
    internal void OnNewSample(int interpolationSteps)
    {
        if (_disposed) return;

        _prevPeakMin = _displayPeakMin;
        _prevPeakMax = _displayPeakMax;
        _targetPeakMin = _rawPeakMin;
        _targetPeakMax = _rawPeakMax;
        _interpolationStep = 0;
        _interpolationSteps = interpolationSteps < 1 ? 1 : interpolationSteps;

        for (int i = _groups.Count - 1; i >= 0; i--) _groups[i].OnNewSample(interpolationSteps);
    }

    /// <summary>
    /// Render-timer callback. Advances the shared step counter and writes the lerped min/max
    /// peaks into the display fields, firing PropertyChanged on actual change so both bound
    /// meter borders redraw every frame. UI-thread.
    /// </summary>
    internal void OnRenderTick()
    {
        if (_disposed) return;

        _interpolationStep++;

        float newMin, newMax;
        if (_interpolationStep >= _interpolationSteps)
        {
            // Reached or passed the targets - snap so a long render-only burst (e.g. paused
            // samples) can't drift past the most recent sample.
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

        for (int i = _groups.Count - 1; i >= 0; i--) _groups[i].OnRenderTick();
    }

    private void EnumerateExistingSessions()
    {
        IAudioSessionEnumerator? enumerator = null;
        try
        {
            _sessionManager.GetSessionEnumerator(out enumerator);
            enumerator.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                enumerator.GetSession(i, out IAudioSessionControl ctrl);
                AddSession(ctrl);
            }
        }
        catch
        {
            // Best-effort - device may have disconnected mid-enumeration.
        }
        finally
        {
            if (enumerator != null) Marshal.FinalReleaseComObject(enumerator);
        }
    }

    private void AddSession(IAudioSessionControl ctrl)
    {
        AudioSession session;
        try { session = new AudioSession(ctrl, _dispatcher, _volumeThrottler, _processExitMonitor); }
        catch
        {
            // Construction can fail if the session is already torn down (race with OnSessionCreated);
            // drop the COM ref and move on.
            try { Marshal.FinalReleaseComObject(ctrl); } catch { }
            return;
        }

        // Dedup: register-then-enumerate in the ctor opens a window where the same COM session is
        // both notified and enumerated. Drop the duplicate wrapper rather than letting two run.
        string key = session.SessionInstanceId;
        if (key.Length > 0 && _sessionsByInstanceId.ContainsKey(key))
        {
            try { session.Dispose(); } catch { }
            return;
        }

        session.Disconnected += OnSessionDisconnected;
        session.StateChanged += OnSessionStateChanged;
        if (key.Length > 0) _sessionsByInstanceId[key] = session;

        // Route into the matching group by AppId, or create a new group when this is the first
        // session for the app. Linear scan is fine - typical session counts are well under a dozen.
        AudioAppGroup? group = null;
        for (int i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].AppId == session.AppId) { group = _groups[i]; break; }
        }
        if (group == null)
        {
            // Populate the group with its first session BEFORE publishing it to the observable
            // collection. _groups.Add fires CollectionChanged synchronously, which the flyout uses
            // to rebuild its visible list - if the group is published while still empty, any
            // "skip empty groups" filter on the consumer side would hide the new app entirely
            // until the next unrelated _groups change forced another rebuild.
            group = new AudioAppGroup(session.AppId, _dispatcher);
            group.Empty += OnGroupEmpty;
            group.AddSession(session);
            _groups.Add(group);
        }
        else
        {
            group.AddSession(session);
        }
    }

    private void OnSessionDisconnected(AudioSession session) => RemoveSession(session);

    private void OnSessionStateChanged(AudioSession session)
    {
        // Expired state means the app stopped using the endpoint; drop it so the flyout list stays current.
        if (session.State == AudioSessionState.Expired) RemoveSession(session);
    }

    private void RemoveSession(AudioSession session)
    {
        string key = session.SessionInstanceId;
        if (key.Length > 0) _sessionsByInstanceId.Remove(key);

        // Find the owning group by walking the list. Sessions can only belong to one group.
        for (int i = 0; i < _groups.Count; i++)
        {
            AudioAppGroup g = _groups[i];
            if (!g.Sessions.Contains(session)) continue;

            session.Disconnected -= OnSessionDisconnected;
            session.StateChanged -= OnSessionStateChanged;
            g.RemoveSession(session);
            try { session.Dispose(); } catch { }
            return;
        }
    }

    private void OnGroupEmpty(AudioAppGroup group)
    {
        group.Empty -= OnGroupEmpty;
        _groups.Remove(group);
        try { group.Dispose(); } catch { }
    }

    /// <summary>
    /// Re-read PKEY_Device_FriendlyName from the property store and update the bindable
    /// <see cref="FriendlyName"/>. Invoked by the manager when the OS reports a friendly-name
    /// change for this endpoint - in-place update keeps the device wrapper alive instead of
    /// triggering the destructive RebuildDeviceList path.
    /// </summary>
    internal void RefreshFriendlyNameFromStore()
    {
        if (_disposed) return;
        FriendlyName = ResolveFriendlyName(_device);
    }

    private static string ResolveFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;
            store.GetValue(ref key, out PROPVARIANT pv);
            try { return pv.GetString() ?? "Unknown Device"; }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch
        {
            return "Unknown Device";
        }
        finally
        {
            if (store != null) Marshal.FinalReleaseComObject(store);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drop any queued endpoint-volume write so the throttler driver doesn't try to call
        // SetMasterVolumeLevelScalar on the about-to-be-released RCW.
        try { _volumeThrottler.Drop(_volumeThrottlerKey); } catch { }

        try { _endpointVolume.UnregisterControlChangeNotify(_volumeBridge); } catch { }
        try { _sessionManager.UnregisterSessionNotification(_sessionBridge); } catch { }

        // Tear down every group's sessions, then the groups themselves. Each group disposes its
        // session subscriptions; we still own the AudioSession lifecycle (Dispose/Disconnect handlers).
        foreach (AudioAppGroup group in _groups.ToArray())
        {
            group.Empty -= OnGroupEmpty;
            foreach (AudioSession session in group.Sessions.ToArray())
            {
                session.Disconnected -= OnSessionDisconnected;
                session.StateChanged -= OnSessionStateChanged;
                try { session.Dispose(); } catch { }
            }
            try { group.Dispose(); } catch { }
        }
        _groups.Clear();
        _sessionsByInstanceId.Clear();

        TryRelease(_endpointVolume);
        TryRelease(_endpointMeter);
        TryRelease(_sessionManager);
        TryRelease(_device);
    }

    private static void TryRelease(object? rcw)
    {
        if (rcw == null) return;
        try { Marshal.FinalReleaseComObject(rcw); } catch { }
    }

    // Endpoint volume / mute callbacks. Marshal native AUDIO_VOLUME_NOTIFICATION_DATA from the
    // raw pointer; ignoring the trailing variable-length per-channel array since we only track master.
    private sealed class EndpointVolumeBridge : IAudioEndpointVolumeCallback
    {
        private readonly AudioDevice _owner;
        public EndpointVolumeBridge(AudioDevice owner) { _owner = owner; }

        public int OnNotify(IntPtr pNotify)
        {
            if (pNotify == IntPtr.Zero) return 0;
            AUDIO_VOLUME_NOTIFICATION_DATA data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);

            // Suppress echoes from our own writes.
            if (data.guidEventContext == EventContext) return 0;

            float vol = data.fMasterVolume;
            bool muted = data.bMuted;

            _owner._dispatcher.BeginInvoke(() =>
            {
                if (Math.Abs(vol - _owner._volume) >= 0.0005f)
                {
                    _owner._volume = vol;
                    _owner.OnPropertyChanged(nameof(Volume));
                }
                if (muted != _owner._isMuted)
                {
                    _owner._isMuted = muted;
                    _owner.OnPropertyChanged(nameof(IsMuted));
                }
            });
            return 0;
        }
    }

    private sealed class SessionNotificationBridge : IAudioSessionNotification
    {
        private readonly AudioDevice _owner;
        public SessionNotificationBridge(AudioDevice owner) { _owner = owner; }

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            // The COM ref count of newSession is given to us; AddSession takes ownership.
            _owner._dispatcher.BeginInvoke(() => _owner.AddSession(newSession));
            return 0;
        }
    }
}
