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
    // Endpoint COM proxies are only available on Active devices; on Disabled / NotPresent /
    // Unplugged endpoints, IMMDevice.Activate(IAudioEndpointVolume) returns AUDCLNT_E_DEVICE_INVALIDATED.
    // We still want a managed wrapper for those devices so they can be listed in the tray menu, set
    // as default, or re-enabled - hence the nullable proxies + UpgradeFromActiveState which retries
    // activation when the OS reports the device transitioned to Active.
    private IAudioEndpointVolume? _endpointVolume;
    private IAudioMeterInformation? _endpointMeter;
    private IAudioSessionManager2? _sessionManager;
    private EndpointVolumeBridge? _volumeBridge;
    private SessionNotificationBridge? _sessionBridge;
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
    private string _deviceDescription;
    private string _interfaceFriendlyName;
    private float _volume;
    private bool _isMuted;
    private bool _isDefault;
    private bool _isDefaultCommunications;
    private bool _isListeningToThisDevice;
    private string? _listenTargetDeviceId;
    private bool _isListenTargetActive;
    private DeviceState _state;
    private bool _disposed;

    // Single-flight gate for IPolicyConfig calls on this device. SetEnabled / SetAsDefault /
    // SetAsDefaultCommunications all share this flag so a rapid click that would otherwise fire
    // two overlapping brokered calls into the audio service (each blocking hundreds of ms) drops
    // the second one outright. The COM call itself is the verification that the state landed -
    // we clear the flag in the Task.Run's finally block once the audio service has acknowledged
    // the write.
    // Interlocked over a Lock since the only operation is "test-and-set with a single-shot
    // release" - lock/unlock would just be ceremony.
    private int _policyConfigCallInFlight;

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

    // True on a capture endpoint when no session is currently in the Active state. Windows lets
    // the capture engine idle when no app is streaming the mic, so the endpoint meter stops
    // updating and holds whatever peak was last sampled. Bg-thread sample reads see this flag and
    // force the raw peaks to 0 so the lerp falls to silence instead of freezing on a stale value;
    // the bound UI also swaps the mute-row glyph to MICROPHONE_SLEEP. volatile so the bg sample
    // thread reads a coherent value - writes always happen on the UI thread.
    private volatile bool _isCaptureSleeping;

    public string Id { get; }
    public EDataFlow DataFlow { get; }
    public ReadOnlyObservableCollection<AudioAppGroup> Groups { get; }

    public string FriendlyName
    {
        get => _friendlyName;
        private set { if (_friendlyName != value) { _friendlyName = value; OnPropertyChanged(); } }
    }

    // Endpoint description from PKEY_Device_DeviceDesc, e.g. "Speakers" or "Headphones".
    // The "Name" half of the FriendlyName composite "Speakers (Realtek(R) Audio)".
    // Falls back to FriendlyName when the property store has no DeviceDesc entry.
    public string DeviceDescription
    {
        get => _deviceDescription;
        private set { if (_deviceDescription != value) { _deviceDescription = value; OnPropertyChanged(); } }
    }

    // Adapter / interface name from PKEY_DeviceInterface_FriendlyName, e.g. "Realtek(R) Audio".
    // The "Model" half of the FriendlyName composite. Falls back to FriendlyName when the
    // property store has no DeviceInterface entry.
    public string InterfaceFriendlyName
    {
        get => _interfaceFriendlyName;
        private set { if (_interfaceFriendlyName != value) { _interfaceFriendlyName = value; OnPropertyChanged(); } }
    }

    public bool IsDefault
    {
        get => _isDefault;
        internal set { if (_isDefault != value) { _isDefault = value; OnPropertyChanged(); } }
    }

    public bool IsDefaultCommunications
    {
        get => _isDefaultCommunications;
        internal set { if (_isDefaultCommunications != value) { _isDefaultCommunications = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Current device state from MMDevice (Active / Disabled / Unplugged / NotPresent).
    /// Refreshed in place on every IMMNotificationClient.OnDeviceStateChanged for this id so
    /// visibility filters in the flyout / tray menu reflect the live OS state without a rebuild.
    /// Raises PropertyChanged for the derived bool projections (IsActive / IsDisabled / IsDisconnected
    /// / IsNotPresent) too - they read State synthetically and would otherwise stay stale in the UI
    /// since WPF only re-evaluates a binding when its bound path raises a notification.
    /// </summary>
    public DeviceState State
    {
        get => _state;
        internal set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(IsNotPresent));
        }
    }

    public bool IsActive => (State & DeviceState.Active) != 0;
    public bool IsDisabled => (State & DeviceState.Disabled) != 0;
    public bool IsDisconnected => (State & (DeviceState.Unplugged | DeviceState.NotPresent)) != 0;

    // Convenience flag for XAML triggers / converters that need to branch on device direction
    // without dragging the internal EDataFlow enum into the binding layer.
    public bool IsCaptureDevice => DataFlow == EDataFlow.eCapture;

    /// <summary>
    /// Mirrors the Sound Control Panel "Listen to this device" checkbox for capture endpoints.
    /// Read on construction from the endpoint property store and refreshed in place when the OS
    /// reports a change via IMMNotificationClient.OnPropertyValueChanged. Always false on render
    /// endpoints - the underlying property only exists on capture devices.
    /// </summary>
    public bool IsListeningToThisDevice
    {
        get => _isListeningToThisDevice;
        private set
        {
            if (_isListeningToThisDevice == value) return;
            _isListeningToThisDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListenButtonActive));
        }
    }

    /// <summary>
    /// IMMDevice id of the render endpoint that the Listen feature feeds audio to on this capture
    /// endpoint. Null means follow the system default playback device - mmsys.cpl encodes this by
    /// deleting the pid (PROPVARIANT VT_EMPTY) and we mirror that on writes.
    /// </summary>
    public string? ListenTargetDeviceId
    {
        get => _listenTargetDeviceId;
        private set
        {
            if (string.Equals(_listenTargetDeviceId, value, StringComparison.Ordinal)) return;
            _listenTargetDeviceId = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Cached "the device we listen through right now is up" flag. Pushed in by
    /// <see cref="AudioDeviceManager"/> whenever the target endpoint's State, the system default
    /// playback device, or this capture device's <see cref="ListenTargetDeviceId"/> changes, so
    /// the flyout's button-dim binding can react without doing cross-device lookup itself.
    /// Always false on render endpoints.
    /// </summary>
    public bool IsListenTargetActive
    {
        get => _isListenTargetActive;
        internal set
        {
            if (_isListenTargetActive == value) return;
            _isListenTargetActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListenButtonActive));
        }
    }

    /// <summary>
    /// True iff Listen is enabled AND the routed playback target is currently active. The flyout's
    /// listen-button opacity binds here so the button reads dim whenever there's nothing audible
    /// happening - either Listen is off, or it's on but pointing at a disabled / disconnected target.
    /// </summary>
    public bool IsListenButtonActive => _isListeningToThisDevice && _isListenTargetActive;

    // Registry-only ghost: the endpoint exists in the user's audio device registry but no driver
    // is currently loaded for it. NotPresent endpoints accumulate across years of plugged USB DACs,
    // GPU swaps with HDMI audio, paired Bluetooth headsets, etc. Their FriendlyName usually fails
    // to resolve and falls back to the literal "Unknown Device".
    public bool IsNotPresent => (State & DeviceState.NotPresent) != 0;

    // Process-wide IPolicyConfig client. The COM object is cheap and reusable so we cache it
    // across calls. Lazy keeps non-default-switching sessions from paying the CoCreateInstance
    // cost; ExecutionAndPublication makes the lazy init safe under concurrent first access from
    // the threadpool callbacks below.
    private static readonly Lazy<IPolicyConfig> PolicyConfigClient = new(
        () => (IPolicyConfig)new PolicyConfigClientComObject(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Promotes this endpoint to the system default. Mirrors mmsys.cpl's "Set as Default Device":
    /// both Console (system sounds) and Multimedia roles are switched. Communications-role behavior
    /// follows the user's <see cref="AppSettings.SetDefaultCommsToDefault"/> preference - on, we mirror
    /// the multimedia default into the communications role here so a single click ties them together;
    /// off, the user's separate comms choice is preserved.
    /// The COM call is brokered to the audio service synchronously and the audio service's reply
    /// fans out a notification storm before returning - blocks the UI thread for hundreds of ms,
    /// up to seconds when the default changes. Off-loaded to the threadpool so the dispatcher stays
    /// responsive; the resulting OnDefaultDeviceChanged notifications coalesce through
    /// AudioDeviceManager.ScheduleUpdateAllDefaults.
    /// Drops outright if another IPolicyConfig call is already in flight for this device - a rapid
    /// follow-up click would otherwise stack a second multi-second blocked threadpool task with no
    /// way to verify the first one has actually landed.
    /// </summary>
    internal void SetAsDefault()
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        // Snapshot the closure-captured fields so a Dispose racing with this call can't NRE on
        // FriendlyName / Id field reads after the wrapper is torn down.
        string id = Id;
        string friendlyName = FriendlyName;
        bool mirrorComms = AppServices.Settings?.SetDefaultCommsToDefault == true;

        RunPolicyConfigCall(client =>
        {
            client.SetDefaultEndpoint(id, ERole.eConsole);
            client.SetDefaultEndpoint(id, ERole.eMultimedia);
            if (mirrorComms) client.SetDefaultEndpoint(id, ERole.eCommunications);
        }, $"SetAsDefault({friendlyName})");
    }

    /// <summary>
    /// Promotes this endpoint to the communications-role default only. One-way trip - never reverts
    /// the user's other-role defaults. Bound to shift+click on the device icon in the flyout.
    /// Off-loaded to the threadpool and gated by the same single-flight check as SetAsDefault.
    /// </summary>
    internal void SetAsDefaultCommunications()
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        string id = Id;
        string friendlyName = FriendlyName;

        RunPolicyConfigCall(client => client.SetDefaultEndpoint(id, ERole.eCommunications),
            $"SetAsDefaultCommunications({friendlyName})");
    }

    /// <summary>
    /// Toggles endpoint visibility through IPolicyConfig. true enables the device (eq. mmsys.cpl
    /// "Enable"); false disables it. The device list / state callbacks pick up the resulting
    /// transition through OnDeviceStateChanged - we never tweak State locally.
    /// Off-loaded to the threadpool because the call blocks while the audio service rewrites its
    /// endpoint table and fans out the resulting notification storm to every audio-aware app on
    /// the system - several hundred ms in the common case, multi-second when the default device
    /// is the one being disabled.
    /// Single-flight gated so a rapid double-click can't queue a second blocking call before the
    /// first has been acknowledged.
    /// </summary>
    internal void SetEnabled(bool enabled)
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        string id = Id;
        string friendlyName = FriendlyName;
        short visibility = enabled ? (short)1 : (short)0;

        RunPolicyConfigCall(client => client.SetEndpointVisibility(id, visibility),
            $"SetEnabled({friendlyName}, {enabled})");
    }

    /// <summary>
    /// Single-flight runner for an IPolicyConfig action against this device. Returns immediately
    /// (drops the action) when another IPolicyConfig call is already in flight for the same device;
    /// otherwise dispatches the action onto the threadpool, clearing the flag in finally so a
    /// thrown COM exception doesn't permanently strand the gate.
    /// The flag is shared across SetEnabled / SetAsDefault / SetAsDefaultCommunications because
    /// the audio service serializes them anyway and the user-perceived gesture is "any click on
    /// the device icon counts as one in-flight operation".
    /// </summary>
    private void RunPolicyConfigCall(Action<IPolicyConfig> action, string callDescription)
    {
        if (Interlocked.CompareExchange(ref _policyConfigCallInFlight, 1, 0) != 0)
        {
            WPFLog.Log($"AudioDevice.{callDescription}: dropped (prior IPolicyConfig call still in flight)");
            return;
        }

        _ = Task.Run(() =>
        {
            try { action(PolicyConfigClient.Value); }
            catch (Exception ex) { WPFLog.Log($"AudioDevice.{callDescription}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _policyConfigCallInFlight, 0); }
        });
    }

    public float Volume
    {
        get => _volume;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(clamped - _volume) < 0.0005f) return;
            // No endpoint volume proxy on disabled / unplugged devices - bail before churning the UI.
            IAudioEndpointVolume? proxy = _endpointVolume;
            if (proxy == null) return;

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
                    proxy.SetMasterVolumeLevelScalar(captured, ref ctx);
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
            IAudioEndpointVolume? proxy = _endpointVolume;
            if (proxy == null) return;

            try
            {
                Guid ctx = EventContext;
                proxy.SetMute(value, ref ctx);
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

    /// <summary>
    /// True when this is a capture endpoint with no currently-Active session. Windows idles the
    /// capture engine in that state and the endpoint meter freezes on its last value, so the UI
    /// swaps to MICROPHONE_SLEEP and the sample loop pins the peak to 0. Always false on render
    /// endpoints. Recomputed on every session add / remove / state change.
    /// </summary>
    public bool IsCaptureSleeping => _isCaptureSleeping;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AudioDevice(IMMDevice device, EDataFlow dataFlow, Dispatcher dispatcher, AsyncThrottler<string> volumeThrottler, ProcessExitMonitor processExitMonitor)
    {
        _device = device;
        DataFlow = dataFlow;
        _dispatcher = dispatcher;
        _volumeThrottler = volumeThrottler;
        _processExitMonitor = processExitMonitor;
        Groups = new ReadOnlyObservableCollection<AudioAppGroup>(_groups);

        device.GetId(out string id);
        Id = id ?? string.Empty;
        _volumeThrottlerKey = "endpoint:" + Id;

        (_friendlyName, _deviceDescription, _interfaceFriendlyName) = ResolveDeviceNames(device);

        device.GetState(out uint stateRaw);
        _state = (DeviceState)stateRaw;

        // Listen-to-this-device is a capture-only feature. Seed the fields directly so the bound
        // UI doesn't need a PropertyChanged round-trip on first paint; runtime changes flow
        // through RefreshListenStateFromStore via the manager's property-change callback.
        if (DataFlow == EDataFlow.eCapture)
        {
            (_isListeningToThisDevice, _listenTargetDeviceId) = ReadListenStateFromStore(_device);
        }

        // Endpoint volume / meter / session manager are only addressable on Active devices.
        // For Disabled / NotPresent / Unplugged endpoints we keep a thin wrapper alive so the
        // tray menu and visibility filters can still reason about them; UpgradeFromActiveState
        // re-tries activation when the OS later reports the device active.
        if (IsActive) TryActivateProxies();
    }

    /// <summary>
    /// Re-read the listen-feature property pair (pid 1 = enable bool, pid 0 = target endpoint id)
    /// from the property store and update <see cref="IsListeningToThisDevice"/> +
    /// <see cref="ListenTargetDeviceId"/>. No-op on render endpoints. Invoked by the manager when
    /// OnPropertyValueChanged fires for either listen-fmtid pid on this device id.
    /// </summary>
    internal void RefreshListenStateFromStore()
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;
        (bool enabled, string? target) = ReadListenStateFromStore(_device);
        IsListeningToThisDevice = enabled;
        ListenTargetDeviceId = target;
    }

    /// <summary>
    /// Writes the listen-enable bit (pid 1) to the endpoint property store. Leaves the target
    /// (pid 0) untouched - the audio service falls back to whatever target was previously chosen,
    /// matching the user's expectation that toggling on doesn't replace their target selection.
    /// No-op on render endpoints.
    /// </summary>
    internal void SetListenEnabled(bool enabled)
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;
        WriteListenBool(PropertyKeys.PKEY_AudioEndpoint_ListenToThisDevice, enabled);
        RefreshListenStateFromStore();
    }

    /// <summary>
    /// Writes both the listen-target id (pid 0) and the listen-enable bit (pid 1) in one commit.
    /// Passing null for <paramref name="targetDeviceId"/> deletes pid 0 (VT_EMPTY) which mmsys.cpl
    /// reads back as 'Default Playback Device' - the audio service will follow whichever render
    /// endpoint is currently default. No-op on render endpoints.
    /// </summary>
    internal void SetListenTarget(string? targetDeviceId, bool enable)
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;

        IPropertyStore? store = null;
        IntPtr targetPtr = IntPtr.Zero;
        try
        {
            _device.OpenPropertyStore(1 /* STGM_WRITE */, out store);

            PROPERTYKEY targetKey = PropertyKeys.PKEY_AudioEndpoint_ListenTargetDeviceId;
            PROPVARIANT targetPv = default;
            if (string.IsNullOrEmpty(targetDeviceId))
            {
                targetPv.vt = PROPVARIANT.VT_EMPTY;
            }
            else
            {
                targetPtr = Marshal.StringToCoTaskMemUni(targetDeviceId);
                targetPv.vt = PROPVARIANT.VT_LPWSTR;
                targetPv.p1 = targetPtr;
            }
            store.SetValue(ref targetKey, ref targetPv);

            PROPERTYKEY enableKey = PropertyKeys.PKEY_AudioEndpoint_ListenToThisDevice;
            PROPVARIANT enablePv = default;
            enablePv.vt = PROPVARIANT.VT_BOOL;
            // VT_BOOL is VARIANT_BOOL: -1 (0xFFFF) for TRUE, 0 for FALSE. Stored in p1's low word.
            enablePv.p1 = enable ? new IntPtr(unchecked((int)0xFFFF)) : IntPtr.Zero;
            store.SetValue(ref enableKey, ref enablePv);

            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.SetListenTarget({FriendlyName}): {ex.Message}");
            return;
        }
        finally
        {
            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (store != null) Marshal.FinalReleaseComObject(store);
        }

        RefreshListenStateFromStore();
    }

    private void WriteListenBool(PROPERTYKEY key, bool value)
    {
        IPropertyStore? store = null;
        try
        {
            _device.OpenPropertyStore(1 /* STGM_WRITE */, out store);
            PROPVARIANT pv = default;
            pv.vt = PROPVARIANT.VT_BOOL;
            pv.p1 = value ? new IntPtr(unchecked((int)0xFFFF)) : IntPtr.Zero;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.WriteListenBool({FriendlyName}, pid={key.pid}): {ex.Message}");
        }
        finally
        {
            if (store != null) Marshal.FinalReleaseComObject(store);
        }
    }

    private static (bool Enabled, string? TargetDeviceId) ReadListenStateFromStore(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out store);

            PROPERTYKEY enableKey = PropertyKeys.PKEY_AudioEndpoint_ListenToThisDevice;
            store.GetValue(ref enableKey, out PROPVARIANT enablePv);
            bool enabled;
            try
            {
                // Be permissive on the int-ish variants - VT_BOOL is the observed canonical form
                // but older builds and odd drivers have been seen using VT_UI4 / VT_I4 here.
                enabled = enablePv.vt switch
                {
                    PROPVARIANT.VT_BOOL => ((short)(enablePv.p1.ToInt64() & 0xFFFF)) != 0,
                    PROPVARIANT.VT_UI4 => enablePv.GetUInt32() != 0,
                    PROPVARIANT.VT_I4 => enablePv.p1.ToInt64() != 0,
                    _ => false,
                };
            }
            finally { Ole32.PropVariantClear(ref enablePv); }

            PROPERTYKEY targetKey = PropertyKeys.PKEY_AudioEndpoint_ListenTargetDeviceId;
            store.GetValue(ref targetKey, out PROPVARIANT targetPv);
            string? target;
            try { target = targetPv.GetString(); }
            finally { Ole32.PropVariantClear(ref targetPv); }

            return (enabled, target);
        }
        catch { return (false, null); }
        finally { if (store != null) Marshal.FinalReleaseComObject(store); }
    }

    /// <summary>
    /// Attempts the IAudioEndpointVolume / IAudioMeterInformation / IAudioSessionManager2 activation
    /// chain. All three live behind the same WASAPI activation gate, so a single failure path covers
    /// disconnect / disable / unplug. Idempotent - skips fields that are already populated.
    /// </summary>
    private void TryActivateProxies()
    {
        if (_endpointVolume != null) return;

        try
        {
            int hr = _device.Activate(typeof(IAudioEndpointVolume).GUID, ClsCtx.ALL, IntPtr.Zero, out object volObj);
            if (hr < 0 || volObj == null) return;
            IAudioEndpointVolume endpointVolume = (IAudioEndpointVolume)volObj;

            _device.Activate(typeof(IAudioMeterInformation).GUID, ClsCtx.ALL, IntPtr.Zero, out object meterObj);
            _device.Activate(typeof(IAudioSessionManager2).GUID, ClsCtx.ALL, IntPtr.Zero, out object mgrObj);

            _endpointVolume = endpointVolume;
            _endpointMeter = (IAudioMeterInformation)meterObj;
            _sessionManager = (IAudioSessionManager2)mgrObj;

            endpointVolume.GetMasterVolumeLevelScalar(out _volume);
            endpointVolume.GetMute(out _isMuted);
            // Notify bindings that volume/mute now have real values - the wrapper may have lived as
            // an inactive shell up to this point and the bound UI is showing the 0/false defaults.
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(IsMuted));

            _volumeBridge = new EndpointVolumeBridge(this);
            endpointVolume.RegisterControlChangeNotify(_volumeBridge);

            _sessionBridge = new SessionNotificationBridge(this);
            _sessionManager.RegisterSessionNotification(_sessionBridge);

            EnumerateExistingSessions();

            // After the initial session enumeration, decide whether the capture engine is idle.
            // Render endpoints short-circuit inside the recompute - the flag stays false there.
            RecomputeCaptureSleepingState();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.TryActivateProxies({FriendlyName}): {ex.Message}");
        }
    }

    /// <summary>
    /// Re-runs the endpoint activation chain when the OS reports the device transitioned to
    /// Active. No-op if the proxies are already wired (the device was already active). Called by
    /// AudioDeviceManager from its OnDeviceStateChanged path when newState includes the Active bit.
    /// </summary>
    internal void UpgradeFromActiveState()
    {
        if (_endpointVolume == null) TryActivateProxies();
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
        IAudioMeterInformation? meter = _endpointMeter;
        if (meter == null) return;

        if (_isCaptureSleeping)
        {
            // Capture engine is idled by Windows when no app holds an active stream. The endpoint
            // meter holds the last value from when something was capturing - reading it would
            // freeze the level bar on a stale peak. Pin raw peaks to 0 instead; the lerp falls
            // smoothly to silence.
            _rawPeakMin = 0f;
            _rawPeakMax = 0f;
        }
        else
        {
            try
            {
                MeterReader.ReadStereoPeaks(meter, unified, biasMultiplier, out float minPeak, out float maxPeak);
                _rawPeakMin = minPeak;
                _rawPeakMax = maxPeak;
            }
            catch
            {
                // Endpoint disconnect race - leave previous raw values in place; next sample reconciles.
            }
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
        IAudioSessionManager2? mgr = _sessionManager;
        if (mgr == null) return;

        IAudioSessionEnumerator? enumerator = null;
        try
        {
            mgr.GetSessionEnumerator(out enumerator);
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

        // A newly-added Active session wakes the capture engine; recompute so the bound UI flips
        // off MICROPHONE_SLEEP without waiting for the next state-change event.
        RecomputeCaptureSleepingState();
    }

    private void OnSessionDisconnected(AudioSession session) => RemoveSession(session);

    private void OnSessionStateChanged(AudioSession session)
    {
        // Expired state means the app stopped using the endpoint; drop it so the flyout list stays current.
        if (session.State == AudioSessionState.Expired) RemoveSession(session);
        // Any Active <-> Inactive flip can change the capture engine's idle status. RemoveSession
        // also calls Recompute, but it's idempotent (the flag only fires PropertyChanged on actual
        // change) so the double call on the Expired path is harmless.
        RecomputeCaptureSleepingState();
    }

    /// <summary>
    /// Walks the live group list and flips <see cref="IsCaptureSleeping"/> based on whether any
    /// session is currently Active. No-op on render endpoints - the engine never idles there, so
    /// the flag stays false and the bound UI keeps showing the normal speaker tier. Must be called
    /// on the UI thread since the group list is UI-thread-only.
    /// </summary>
    private void RecomputeCaptureSleepingState()
    {
        if (_disposed) return;
        bool sleeping = DataFlow == EDataFlow.eCapture && !HasAnyActiveSession();
        if (_isCaptureSleeping == sleeping) return;
        _isCaptureSleeping = sleeping;
        OnPropertyChanged(nameof(IsCaptureSleeping));
    }

    private bool HasAnyActiveSession()
    {
        for (int i = 0; i < _groups.Count; i++)
        {
            if (_groups[i].State == AudioSessionState.Active) return true;
        }
        return false;
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
            // Losing this session may have been the last active one; refresh the sleep flag.
            RecomputeCaptureSleepingState();
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
    /// Writes a user-chosen override to PKEY_Device_FriendlyName on the endpoint property store,
    /// mirroring the Sound Control Panel rename gesture. Null / empty / whitespace stores a
    /// VT_EMPTY which removes the override - the audio service restores the OS-synthesized name.
    /// IMMNotificationClient.OnPropertyValueChanged from the audio service arrives async and is
    /// sometimes dropped for self-initiated writes, so we reconcile the bindable name here in
    /// place once Commit returns - the eventual notification round-trip is idempotent.
    /// </summary>
    internal void SetCustomFriendlyName(string? name)
    {
        if (_disposed) return;

        string? trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        // No-op when the requested name already matches the rendered name - avoids the COM write,
        // the Commit, and the notification fanout for an edit that wouldn't change anything.
        if (trimmed != null && trimmed == FriendlyName) return;

        IPropertyStore? store = null;
        IntPtr stringPtr = IntPtr.Zero;
        try
        {
            _device.OpenPropertyStore(1 /* STGM_WRITE */, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;

            PROPVARIANT pv = default;
            if (trimmed == null)
            {
                pv.vt = PROPVARIANT.VT_EMPTY;
            }
            else
            {
                stringPtr = Marshal.StringToCoTaskMemUni(trimmed);
                pv.vt = PROPVARIANT.VT_LPWSTR;
                pv.p1 = stringPtr;
            }

            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.SetCustomFriendlyName({FriendlyName}): {ex.Message}");
            return;
        }
        finally
        {
            // SetValue copies the buffer; safe to free our allocation either way.
            if (stringPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(stringPtr);
            if (store != null) Marshal.FinalReleaseComObject(store);
        }

        RefreshFriendlyNameFromStore();
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
        (string composite, string desc, string iface) = ResolveDeviceNames(_device);
        FriendlyName = composite;
        DeviceDescription = desc;
        InterfaceFriendlyName = iface;
    }

    // Reads the trio of name properties off the endpoint property store in one pass:
    // composite FriendlyName, endpoint DeviceDesc ("Speakers"), and DeviceInterface FriendlyName
    // ("Realtek(R) Audio"). Each component falls back to the composite name when its specific
    // property is missing, so the tray menu's Name / Controller views always have something to show.
    private static (string FriendlyName, string DeviceDescription, string InterfaceFriendlyName)
        ResolveDeviceNames(IMMDevice device)
    {
        const string Unknown = "Unknown Device";
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out store);

            string friendly = ReadStringProperty(store, PropertyKeys.PKEY_Device_FriendlyName) ?? Unknown;
            string deviceDesc = ReadStringProperty(store, PropertyKeys.PKEY_Device_DeviceDesc) ?? friendly;
            string iface = ReadStringProperty(store, PropertyKeys.PKEY_DeviceInterface_FriendlyName) ?? friendly;
            return (friendly, deviceDesc, iface);
        }
        catch
        {
            return (Unknown, Unknown, Unknown);
        }
        finally
        {
            if (store != null) Marshal.FinalReleaseComObject(store);
        }
    }

    private static string? ReadStringProperty(IPropertyStore store, PROPERTYKEY key)
    {
        try
        {
            PROPERTYKEY local = key;
            store.GetValue(ref local, out PROPVARIANT pv);
            try { return pv.GetString(); }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch
        {
            return null;
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

        if (_endpointVolume != null && _volumeBridge != null)
        {
            try { _endpointVolume.UnregisterControlChangeNotify(_volumeBridge); } catch { }
        }
        if (_sessionManager != null && _sessionBridge != null)
        {
            try { _sessionManager.UnregisterSessionNotification(_sessionBridge); } catch { }
        }

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
