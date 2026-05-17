using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.Utils;
using Timer = System.Threading.Timer;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Managed wrapper around an output audio endpoint (IMMDevice + Render).
/// Owns the endpoint volume + meter, the session manager, and the live session list for this device.
/// Subscribes to <see cref="IAudioEndpointVolumeCallback"/> for endpoint-level volume / mute changes
/// and to <see cref="IAudioSessionNotification"/> for newly-created sessions.
/// </summary>
internal sealed class AudioDevice : INotifyPropertyChanged, IDisposable
{
    // EventContext / Stgm live in Audio/Interop/AudioInterop.cs. AudioDevice references them
    // through AudioEventContext.Value / Stgm.Read / Stgm.Write.

    // Apartment-state contract for the COM RCWs below:
    //  - _device is the IMMDevice handed to us by IMMDeviceEnumerator on the WPF UI-thread STA.
    //    It MUST NOT be reused on any threadpool worker (the sample timer, EndpointSoundPlayback,
    //    PolicyConfig writes, etc.). Cross-apartment marshalling fails as QueryInterface refusals.
    //    Workers that need an IMMDevice re-acquire one via MMDeviceEnumerator.GetDevice(Id) on
    //    their own thread; EndpointSoundPlayback is the documented example.
    //  - _endpointVolume / _endpointMeter / _simpleVolume (via sessions) / _sessionManager are
    //    activated through _device on the UI thread, but the implementations published by
    //    audioses.dll register the free-threaded marshaler. That's why the sample timer can call
    //    _endpointMeter.GetChannelsPeakValues on the threadpool and the volume throttler can call
    //    SetMasterVolumeLevelScalar from worker tasks - both work today because audioses.dll's
    //    proxies are MTA-compatible. If a future Windows build ever drops the FTM, the meter
    //    reads need to move onto the dispatcher and the throttler payloads need to dispatch
    //    their COM call too. _device access stays UI-thread-only either way.
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

    // Groups live in a public-readable observable collection; AudioDevice mutates it on the UI thread only.
    // One group per AppID; sessions belonging to the same app (Discord's child processes, Chromium tabs,
    // etc.) collate into a single group so the flyout shows one slider per app.
    private readonly ObservableCollection<AudioAppGroup> _groups = [];

    // Dedup index by SessionInstanceID. Same COM session can be delivered twice during the brief
    // window between RegisterSessionNotification and EnumerateExistingSessions in the ctor:
    // OnSessionCreated marshals to the dispatcher, the synchronous enumerate picks the same session
    // up, and without this guard both arrivals create independent AudioSession wrappers around one
    // COM object - double meter polls, double event handlers, two sliders for one stream.
    private readonly Dictionary<string, AudioSession> _sessionsBySessionInstanceID = new(StringComparer.Ordinal);

    private string _friendlyName;
    private string _deviceDescription;
    private string _interfaceFriendlyName;
    private string? _defaultFormat;
    private float _volume;
    private bool _isMuted;
    private bool _isDefault;
    private bool _isDefaultCommunications;
    private bool _isListeningToThisDevice;
    private string? _listenTargetDeviceID;
    private bool _isListenTargetActive;
    private bool _isExclusiveModeAllowed;
    private bool _isExclusiveControlHeld;
    private uint? _exclusiveControlHolderPID;
    private EqualizerAPOState _equalizerAPOState;
    private DeviceState _state;
    private BluetoothCodec? _currentCodec;
    private int? _batteryLevel;
    private bool _isBluetooth;
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

    // Step-counter peak-meter lerp. Shared with AudioSession via the MeterLerp struct - both
    // hosts compose one and call WriteRawPeaks / OnNewSample / OnRenderTick. With Fps > SampleRate
    // the dispatcher updates the lerp multiple times per sample interval - the screen at vsync
    // catches a stepped sequence of intermediate values rather than a snap-to-latest sequence,
    // which is what gives the meter its smoothness.
    private MeterLerp _meterLerp;

    // Watchdog for the Windows IAudioMeterInformation latch on idle render endpoints (A2DP
    // offload makes BT especially prone). UpdatePeakValueBackground re-arms this one-shot timer
    // every time the COM read returns a different (min, max) pair from the previous sample.
    // After MeterStaleWatchdogMs of bit-exact same-value reads the callback flips _meterIsLatched,
    // and subsequent same-value samples force the lerp to silence instead of writing the stale
    // value through. The next genuinely different read clears the flag. volatile so the bg
    // sample thread sees the callback's flip without a fence.
    private readonly Timer _stuckMeterWatchdog;
    private float _lastRawPeakMin;
    private float _lastRawPeakMax;
    private volatile bool _meterIsLatched;

    // Throttled COM-write driver for endpoint volume. Shape shared with AudioSession via the
    // VolumeThrottle composition - the only difference is the COM call (SetMasterVolumeLevelScalar
    // vs SetMasterVolume).
    private readonly VolumeThrottle _volumeWrite;

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

    /// <summary>
    /// True when this endpoint is backed by a Bluetooth radio. Seeded at construction from the
    /// audio endpoint's <c>PKEY_Device_EnumeratorName</c> (reads "BTHENUM" on most BT endpoints)
    /// with a name-substring fallback, but those signals aren't universal - some Win11 drivers
    /// don't propagate the enumerator key to the audio endpoint and strip the "Bluetooth" prefix
    /// from friendly names, so a real BT headset can read as plain "Headphones (WH-1000XM4)" with
    /// no protocol hint. <see cref="AudioDeviceManager"/> promotes the flag to true after
    /// <see cref="BluetoothBatteryMonitor"/> observes a matching <see cref="ContainerId"/> on
    /// the BT Devices class - that match is definitive since the audio endpoint inherits its
    /// container id from the same physical device the BT watcher enumerates. Setter is internal
    /// so only the manager can flip it; the property raises INPC so codec / battery bindings
    /// re-evaluate when the flag is promoted post-construction.
    /// </summary>
    public bool IsBluetooth
    {
        get => _isBluetooth;
        internal set
        {
            if (_isBluetooth == value) return;
            _isBluetooth = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// PnP container id this endpoint belongs to, read from PKEY_Device_ContainerId. Every
    /// interface a single physical device exposes (this audio endpoint, the Bluetooth radio
    /// devnode, HID battery reports, etc.) inherits the same GUID, so this is what
    /// <see cref="BluetoothBatteryMonitor"/> keys its battery map on to attribute a level to
    /// the right wrapper. Null when the property store doesn't carry the key - common for
    /// virtual / synthetic devices and for any non-Bluetooth endpoint we never query against.
    /// Stable for the lifetime of the wrapper.
    /// </summary>
    public Guid? ContainerId { get; }

    /// <summary>
    /// Raw PnP bus enumerator the endpoint sits on, read from <c>PKEY_Device_EnumeratorName</c>
    /// at construction (e.g. <c>"BTHENUM"</c>, <c>"BTHHFENUM"</c>, <c>"BTHLE"</c>,
    /// <c>"HDAUDIO"</c>, <c>"USB"</c>, <c>"TUSBAUDIO_ENUM"</c>, <c>"ROOT"</c>). Empty string
    /// when the property store doesn't carry the key. Used by the
    /// <see cref="AudioDeviceManager"/> promotion path to refuse marking an endpoint as
    /// Bluetooth when its own bus identity contradicts - a Realtek HDAUDIO endpoint that
    /// happens to share a container id with a Bluetooth devnode is still not Bluetooth.
    /// Stable for the lifetime of the wrapper.
    /// </summary>
    public string EnumeratorName { get; }

    /// <summary>
    /// Last A2DP codec the Bluetooth stack negotiated for this endpoint, pushed in by
    /// <see cref="AudioDeviceManager"/> from <see cref="BluetoothCodecMonitor"/>. Always null on
    /// non-Bluetooth endpoints. The Microsoft.Windows.Bluetooth.BthA2dp ETW event the monitor
    /// listens to publishes a single system-wide codec per stream, so when more than one BT
    /// render endpoint is active, the codec is shared across them - one stream at a time.
    /// </summary>
    public BluetoothCodec? CurrentCodec
    {
        get => _currentCodec;
        internal set
        {
            // No equality short-circuit: every push from the monitor's CodecChanged fan-out is
            // treated as the authoritative latest value. Re-asserting the same codec on a
            // freshly-promoted or freshly-Active endpoint is how it catches up to the cached
            // codec without waiting for an A2DP renegotiation that may never arrive.
            _currentCodec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentCodecName));
        }
    }

    /// <summary>
    /// Convenience projection for XAML bindings that don't want to drill into
    /// <see cref="CurrentCodec"/>.<see cref="BluetoothCodec.FriendlyName"/>. Empty string when
    /// no codec is known, which lets a TextBlock collapse via the usual EmptyToVisibility path
    /// without needing a value converter.
    /// </summary>
    public string CurrentCodecName => _currentCodec?.FriendlyName ?? string.Empty;

    /// <summary>
    /// Battery percentage (0-100) most recently reported for the Bluetooth device backing this
    /// endpoint, or null when unknown - the device doesn't report battery, isn't paired, or hasn't
    /// been queried yet. Pushed in by <see cref="AudioDeviceManager"/> from
    /// <see cref="BluetoothBatteryMonitor"/>, keyed by <see cref="ContainerId"/>. Always null on
    /// non-Bluetooth endpoints.
    /// </summary>
    public int? BatteryLevel
    {
        get => _batteryLevel;
        internal set
        {
            if (_batteryLevel == value) return;
            _batteryLevel = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BatteryLevelText));
        }
    }

    /// <summary>
    /// Convenience projection for XAML bindings: "85%" when battery is known, empty string when
    /// not. Lets a TextBlock collapse via EmptyToVisibility without needing a value converter.
    /// </summary>
    public string BatteryLevelText => _batteryLevel.HasValue ? _batteryLevel.Value + "%" : string.Empty;

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

    // Compact summary of PKEY_AudioEngine_DeviceFormat, e.g. "2 channel, 16 bit, 48000 Hz".
    // Null when the property is missing or the blob is too short to parse - bound TextBlocks
    // collapse to empty in that case. Refreshed in place when the OS reports a format change
    // from the Sound Control Panel's Advanced tab.
    public string? DefaultFormat
    {
        get => _defaultFormat;
        private set { if (_defaultFormat != value) { _defaultFormat = value; OnPropertyChanged(); } }
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
    public string? ListenTargetDeviceID
    {
        get => _listenTargetDeviceID;
        private set
        {
            if (string.Equals(_listenTargetDeviceID, value, StringComparison.Ordinal)) return;
            _listenTargetDeviceID = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Cached "the device we listen through right now is up" flag. Pushed in by
    /// <see cref="AudioDeviceManager"/> whenever the target endpoint's State, the system default
    /// playback device, or this capture device's <see cref="ListenTargetDeviceID"/> changes, so
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

    /// <summary>
    /// Mirrors mmsys.cpl Advanced > "Allow applications to take exclusive control of this device".
    /// Drives the flyout's exclusive-mode button between full-bright (allowed) and dim (disallowed).
    /// Seeded from the endpoint property store in the ctor and refreshed by
    /// <see cref="RefreshAllowExclusiveControlFromStore"/> whenever mmsys.cpl or our own toggle
    /// path writes the underlying VT_UI4 value via PKEY_AudioEndpoint_AllowExclusiveControl.
    /// </summary>
    public bool IsExclusiveModeAllowed
    {
        get => _isExclusiveModeAllowed;
        internal set
        {
            if (_isExclusiveModeAllowed == value) return;
            _isExclusiveModeAllowed = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// True when an audio session on this endpoint is currently holding the device in exclusive
    /// mode (an app called IAudioClient::Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE) and is streaming).
    /// Drives the lock / unlock glyph swap on the device-row button. Detected via the
    /// ExclusiveModeOverride disconnect-reason that all shared sessions on this endpoint receive
    /// when exclusive grabs the device; cleared when a new shared session is created (which can
    /// only happen once exclusive releases). Cold case - exclusive grabs an endpoint that had
    /// no active shared sessions - stays undetected until the next playback attempt.
    /// </summary>
    public bool IsExclusiveControlHeld
    {
        get => _isExclusiveControlHeld;
        internal set
        {
            if (_isExclusiveControlHeld == value) return;
            _isExclusiveControlHeld = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// PID of the process currently holding exclusive control of this endpoint, or null when
    /// nothing is. Bound by the mini-glyph overlay on app icons so the holding app's tile gets
    /// the lock decoration. Stays null while <see cref="IsExclusiveControlHeld"/> backend is stubbed.
    /// </summary>
    public uint? ExclusiveControlHolderPID
    {
        get => _exclusiveControlHolderPID;
        internal set
        {
            if (_exclusiveControlHolderPID == value) return;
            _exclusiveControlHolderPID = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Equalizer APO state for this endpoint. Drives the equalizer-button glyph between
    /// full-bright (Running) and dim (everything else), and selects the click action between
    /// uninstall and install / locate. Stub - always reports the system-wide
    /// <see cref="EqualizerAPOState.NotAvailable"/> until the detection backend lands.
    /// </summary>
    public EqualizerAPOState EqualizerAPOState
    {
        get => _equalizerAPOState;
        internal set
        {
            if (_equalizerAPOState == value) return;
            _equalizerAPOState = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Flips the "Allow applications to take exclusive control" bit on this endpoint by writing
    /// PKEY_AudioEndpoint_AllowExclusiveControl (VT_UI4, 1 = allowed, 0 = disallowed) through
    /// IMMDevice.OpenPropertyStore(STGM_WRITE). The audio service picks up the change on the
    /// next exclusive-mode initialization attempt; shared-mode streams keep running unaffected.
    /// The follow-up Refresh re-reads in case the audio service normalized the value (e.g.
    /// promoted VT_EMPTY) and fires PropertyChanged.
    /// </summary>
    internal void ToggleAllowExclusiveControl()
    {
        if (_disposed) return;
        bool newValue = !_isExclusiveModeAllowed;
        WriteAllowExclusiveControl(newValue);
        RefreshAllowExclusiveControlFromStore();
    }

    /// <summary>
    /// Click action on the equalizer-APO button. Running -> uninstall; EnhancementsOff -> reinstall
    /// (which force-clears the disable-sysfx bit); NotInstalled -> install. NotAvailable is routed
    /// to the install-EAPO dialog before we get here, so we no-op on it.
    /// All registry writes happen synchronously on the caller's thread - they touch HKLM under
    /// MMDevices\...\FxProperties which typically needs admin; without elevation the call raises
    /// UnauthorizedAccessException and we surface that through WPFLog without crashing the UI.
    /// </summary>
    internal void ToggleEqualizerAPO()
    {
        if (_disposed) return;

        string? endpointGuid = TryExtractEndpointGuid(Id);
        if (endpointGuid == null)
        {
            WPFLog.Log($"AudioDevice.ToggleEqualizerAPO({FriendlyName}): no endpoint GUID in '{Id}'");
            return;
        }

        bool isCapture = DataFlow == EDataFlow.eCapture;
        EqualizerAPOState before = EqualizerAPOState;
        WPFLog.Log($"AudioDevice.ToggleEqualizerAPO({FriendlyName}): begin state={before} guid={endpointGuid} capture={isCapture}");

        try
        {
            switch (before)
            {
                case EqualizerAPOState.Running:
                    EqualizerAPOInstaller.Uninstall(endpointGuid, isCapture);
                    // Force the audio engine to reload FxProperties so the EAPO chain stops
                    // processing immediately instead of lingering on every active stream until
                    // each app reinitializes. Interrupts current playback on this endpoint -
                    // intentional: user clicked Off, they want it Off now.
                    ForceCycleEndpoint($"ForceCycleEndpoint({FriendlyName}, after EAPO uninstall)");
                    break;
                case EqualizerAPOState.EnhancementsOff:
                    EqualizerAPOInstaller.Reinstall(endpointGuid, isCapture);
                    break;
                case EqualizerAPOState.NotInstalled:
                    EqualizerAPOInstaller.Install(endpointGuid, isCapture);
                    // Same rationale as the uninstall arm: make the chain audible now rather
                    // than waiting for each app to reinitialize. Interrupts current playback.
                    ForceCycleEndpoint($"ForceCycleEndpoint({FriendlyName}, after EAPO install)");
                    break;
                case EqualizerAPOState.NotAvailable:
                    return;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            WPFLog.Log($"AudioDevice.ToggleEqualizerAPO({FriendlyName}): admin required - {ex.Message}");
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.ToggleEqualizerAPO({FriendlyName}, state={before}): {ex.Message}");
        }

        // Reprobe so the button glyph reflects the new state. Other devices on the same system
        // are unaffected by a per-endpoint toggle, so don't broadcast availability changed here.
        RefreshEqualizerAPOState();
        WPFLog.Log($"AudioDevice.ToggleEqualizerAPO({FriendlyName}): end state={EqualizerAPOState}");
    }

    /// <summary>
    /// Re-probes EAPO state for this endpoint and updates <see cref="EqualizerAPOState"/>. Called
    /// once at construction and again whenever <see cref="EqualizerAPOMonitor.AvailabilityChanged"/>
    /// fires (system-wide install / uninstall) or after our own ToggleEqualizerAPO mutates state.
    /// Probe is synchronous and registry-only, so this is cheap enough to run on the UI thread.
    /// </summary>
    internal void RefreshEqualizerAPOState()
    {
        if (_disposed) return;

        if (!EqualizerAPOMonitor.IsAvailable)
        {
            EqualizerAPOState = EqualizerAPOState.NotAvailable;
            return;
        }

        string? endpointGuid = TryExtractEndpointGuid(Id);
        if (endpointGuid == null)
        {
            EqualizerAPOState = EqualizerAPOState.NotAvailable;
            return;
        }

        DeviceAPOInfo? info = EqualizerAPOInstaller.Probe(endpointGuid, DataFlow == EDataFlow.eCapture);
        if (info == null || !info.IsInstalled)
        {
            EqualizerAPOState = EqualizerAPOState.NotInstalled;
            return;
        }

        EqualizerAPOState = info.EnhancementsDisabled
            ? EqualizerAPOState.EnhancementsOff
            : EqualizerAPOState.Running;
    }

    /// <summary>
    /// Pulls the bare endpoint GUID out of an IMMDevice id of the form
    /// '{0.0.0.00000000}.{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}'. Returns null when the id
    /// doesn't follow that shape (synthetic / test devices).
    /// </summary>
    public static string? TryExtractEndpointGuid(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int separator = id.IndexOf("}.{", StringComparison.Ordinal);
        if (separator < 0) return null;
        return id.Substring(separator + 2);
    }

    private void OnEqualizerAPOAvailabilityChanged()
    {
        // FileSystemWatcher callbacks land on a thread-pool thread; the property setter raises
        // PropertyChanged which must touch WPF state on the dispatcher. BeginInvoke keeps the
        // refresh asynchronous so a Dispose racing with the watcher can drop the call safely.
        try { _dispatcher.BeginInvoke(RefreshEqualizerAPOState); }
        catch (Exception ex) { WPFLog.Log($"AudioDevice.OnEqualizerAPOAvailabilityChanged({FriendlyName}): {ex.Message}"); }
    }

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
        () => (IPolicyConfig)new PolicyConfigClientCOMObject(),
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
    /// Disables and re-enables this endpoint via IPolicyConfig::SetEndpointVisibility, forcing
    /// the audio engine to drop every active session and re-initialize against whatever
    /// FxProperties chain is currently in the registry. Active audio is interrupted by design.
    ///
    /// Default-device preservation: when this endpoint holds eConsole/eMultimedia (IsDefault) or
    /// eCommunications (IsDefaultCommunications), the cycle would normally demote us - Windows
    /// promotes another endpoint when ours is disabled and does NOT restore on re-enable. We
    /// preserve the original assignments by waiting on the audio service's async fanout via
    /// <see cref="AudioDeviceManager.DefaultDeviceChangedRaw"/>: subscribe before disable, wait
    /// for the OS to publish "no longer default for role R" on every role we held, re-enable,
    /// then re-issue SetDefaultEndpoint and wait for the matching "now default for role R"
    /// callbacks. Bounded 2s timeouts on each wait keep a stuck audio service from hanging the
    /// task forever; on timeout we log and proceed.
    ///
    /// Routes through the same threadpool + single-flight gate as the other IPolicyConfig calls.
    /// </summary>
    internal void ForceCycleEndpoint(string callDescription)
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        string id = Id;
        EDataFlow flow = DataFlow;

        if (Interlocked.CompareExchange(ref _policyConfigCallInFlight, 1, 0) != 0)
        {
            WPFLog.Log($"AudioDevice.{callDescription}: dropped (prior IPolicyConfig call still in flight)");
            return;
        }

        // Snapshot role membership before any mutation. SetAsDefault writes both eConsole and
        // eMultimedia together so IsDefault implies the device holds both render-side roles.
        bool wasDefault = IsDefault;
        bool wasDefaultComms = IsDefaultCommunications;

        _ = Task.Run(() =>
        {
            try
            {
                IPolicyConfig client = PolicyConfigClient.Value;

                // Cheap path: device held no default-roles, so the audio service has no
                // promotion fanout to wait on. Give apps a tick to release the device before
                // re-enabling - heuristic, no signal to synchronize against here.
                if (!wasDefault && !wasDefaultComms)
                {
                    client.SetEndpointVisibility(id, 0);
                    Thread.Sleep(250);
                    client.SetEndpointVisibility(id, 1);
                    return;
                }

                // Roles we expect callbacks for: Console + Multimedia (from wasDefault) plus
                // Communications (from wasDefaultComms). Each role transition emits one
                // OnDefaultDeviceChanged on disable (demotion) and one more on restore (promotion
                // back to us).
                int expected = (wasDefault ? 2 : 0) + (wasDefaultComms ? 1 : 0);

                // Phase 1: subscribe BEFORE disable so the demotion fanout can't slip past us.
                // Predicate: "callback for our flow + a role we held, with a new id that isn't
                // ours" - the audio service may publish empty/null for new id when no
                // replacement exists, treat that as a valid demotion signal too.
                int seenPromotions = 0;
                ManualResetEventSlim promotionDone = new(false);
                void OnPromotion(EDataFlow f, ERole r, string? newId)
                {
                    if (f != flow) return;
                    if (string.Equals(newId, id, StringComparison.OrdinalIgnoreCase)) return;
                    bool relevant = (wasDefault && (r == ERole.eConsole || r == ERole.eMultimedia))
                        || (wasDefaultComms && r == ERole.eCommunications);
                    if (!relevant) return;
                    if (Interlocked.Increment(ref seenPromotions) >= expected) promotionDone.Set();
                }
                AudioDeviceManager.DefaultDeviceChangedRaw += OnPromotion;
                try
                {
                    client.SetEndpointVisibility(id, 0);
                    if (!promotionDone.Wait(2000))
                        WPFLog.Log($"AudioDevice.{callDescription}: timed out waiting for default-promotion fanout");
                }
                finally { AudioDeviceManager.DefaultDeviceChangedRaw -= OnPromotion; }

                // Phase 2: re-enable. The audio service serializes IPolicyConfig calls so this
                // queues after the disable it just finished publishing - no inter-call sleep
                // needed once the fanout above completes.
                client.SetEndpointVisibility(id, 1);

                // Phase 3: restore. Subscribe, issue SetDefaultEndpoint for each role we held,
                // wait for the matching "now default for R" callbacks. Predicate flipped: we
                // want callbacks where the new id IS ours.
                int seenRestores = 0;
                ManualResetEventSlim restoreDone = new(false);
                void OnRestore(EDataFlow f, ERole r, string? newId)
                {
                    if (f != flow) return;
                    if (!string.Equals(newId, id, StringComparison.OrdinalIgnoreCase)) return;
                    bool relevant = (wasDefault && (r == ERole.eConsole || r == ERole.eMultimedia))
                        || (wasDefaultComms && r == ERole.eCommunications);
                    if (!relevant) return;
                    if (Interlocked.Increment(ref seenRestores) >= expected) restoreDone.Set();
                }
                AudioDeviceManager.DefaultDeviceChangedRaw += OnRestore;
                try
                {
                    if (wasDefault)
                    {
                        client.SetDefaultEndpoint(id, ERole.eConsole);
                        client.SetDefaultEndpoint(id, ERole.eMultimedia);
                    }
                    if (wasDefaultComms) client.SetDefaultEndpoint(id, ERole.eCommunications);

                    if (!restoreDone.Wait(2000))
                        WPFLog.Log($"AudioDevice.{callDescription}: timed out waiting for default-restore confirmation");
                }
                finally { AudioDeviceManager.DefaultDeviceChangedRaw -= OnRestore; }
            }
            catch (Exception ex) { WPFLog.Log($"AudioDevice.{callDescription}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _policyConfigCallInFlight, 0); }
        });
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

            _volumeWrite.Write(clamped, (v, ctx) => proxy.SetMasterVolumeLevelScalar(v, ref ctx));
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
                Guid ctx = AudioEventContext.Value;
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
    public float PeakValueMin => _meterLerp.DisplayMin;

    /// <summary>
    /// Smoothed max(L, R) peak. Drives the stereo overlay bar that paints on top of the base bar
    /// and extends to the loudest channel. For mono streams this equals <see cref="PeakValueMin"/>.
    /// </summary>
    public float PeakValueMax => _meterLerp.DisplayMax;

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
        _volumeWrite = new VolumeThrottle(volumeThrottler, "endpoint:" + Id);

        (_friendlyName, _deviceDescription, _interfaceFriendlyName) = ResolveDeviceNames(device);
        _defaultFormat = ResolveDefaultFormat(device);
        EnumeratorName = ReadEnumeratorName(device) ?? string.Empty;
        IsBluetooth = DetectIsBluetooth(EnumeratorName, _friendlyName, _deviceDescription, _interfaceFriendlyName);
        ContainerId = ReadContainerId(device);

        device.GetState(out uint stateRaw);
        _state = (DeviceState)stateRaw;

        // Listen-to-this-device is a capture-only feature. Seed the fields directly so the bound
        // UI doesn't need a PropertyChanged round-trip on first paint; runtime changes flow
        // through RefreshListenStateFromStore via the manager's property-change callback.
        if (DataFlow == EDataFlow.eCapture) (_isListeningToThisDevice, _listenTargetDeviceID) = ReadListenStateFromStore(_device);

        // Exclusive-mode "allow" bit lives on both render and capture endpoints (mmsys.cpl shows
        // the Advanced > Exclusive Mode checkbox for either). Seed direct, then refresh runs on
        // mmsys.cpl writes via the manager's OnPropertyValueChanged hook.
        _isExclusiveModeAllowed = ReadAllowExclusiveControlFromStore(_device);

        // One-shot stuck-meter watchdog. Stays disarmed until the first different peak pair lands;
        // every fresh pair after that re-arms it via Timer.Change. Initialized before
        // TryActivateProxies so a fast first sample can't race the field.
        _stuckMeterWatchdog = new Timer(OnStuckMeterWatchdog, null, Timeout.Infinite, Timeout.Infinite);

        // Endpoint volume / meter / session manager are only addressable on Active devices.
        // For Disabled / NotPresent / Unplugged endpoints we keep a thin wrapper alive so the
        // tray menu and visibility filters can still reason about them; UpgradeFromActiveState
        // re-tries activation when the OS later reports the device active.
        if (IsActive) TryActivateProxies();

        // EAPO state seed + live subscription. Probe is registry-only so it's safe on the ctor
        // thread; AvailabilityChanged fires from a FileSystemWatcher worker, so the handler
        // marshals back to the dispatcher.
        EqualizerAPOMonitor.AvailabilityChanged += OnEqualizerAPOAvailabilityChanged;
        RefreshEqualizerAPOState();
    }

    /// <summary>
    /// Re-read the listen-feature property pair (pid 1 = enable bool, pid 0 = target endpoint id)
    /// from the property store and update <see cref="IsListeningToThisDevice"/> +
    /// <see cref="ListenTargetDeviceID"/>. No-op on render endpoints. Invoked by the manager when
    /// OnPropertyValueChanged fires for either listen-fmtid pid on this device id.
    /// </summary>
    internal void RefreshListenStateFromStore()
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;
        (bool enabled, string? target) = ReadListenStateFromStore(_device);
        IsListeningToThisDevice = enabled;
        ListenTargetDeviceID = target;
    }

    /// <summary>
    /// Writes the listen-enable bit (pid 1) to the endpoint property store. Leaves the target
    /// (pid 0) untouched - the audio service falls back to whatever target was previously chosen,
    /// matching the user's expectation that toggling on doesn't replace their target selection.
    /// When enabling, also force-clears the Disable_SysFx bit so the listen monitor isn't muted
    /// by a stale "Disable all enhancements" checkbox - the audio engine routes the monitor
    /// through the sysfx pipeline and a 1 there silently breaks the listen path.
    /// No-op on render endpoints.
    /// </summary>
    internal void SetListenEnabled(bool enabled)
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;
        if (enabled) WriteDisableSysFx(false);
        WriteListenBool(PropertyKeys.PKEY_AudioEndpoint_ListenToThisDevice, enabled);
        RefreshListenStateFromStore();
    }

    /// <summary>
    /// Writes both the listen-target id (pid 0) and the listen-enable bit (pid 1) in one commit.
    /// Passing null for <paramref name="targetDeviceID"/> deletes pid 0 (VT_EMPTY) which mmsys.cpl
    /// reads back as 'Default Playback Device' - the audio service will follow whichever render
    /// endpoint is currently default. When enabling, also force-clears the Disable_SysFx bit so
    /// the engine actually emits the monitor (see SetListenEnabled). No-op on render endpoints.
    /// </summary>
    internal void SetListenTarget(string? targetDeviceID, bool enable)
    {
        if (_disposed || DataFlow != EDataFlow.eCapture) return;

        if (enable) WriteDisableSysFx(false);

        IPropertyStore? store = null;
        IntPtr targetPtr = IntPtr.Zero;
        try
        {
            _device.OpenPropertyStore(Stgm.Write, out store);

            PROPERTYKEY targetKey = PropertyKeys.PKEY_AudioEndpoint_ListenTargetDeviceID;
            PROPVARIANT targetPv = default;
            if (string.IsNullOrEmpty(targetDeviceID))
                targetPv.vt = PROPVARIANT.VT_EMPTY;
            else
            {
                targetPtr = Marshal.StringToCoTaskMemUni(targetDeviceID);
                targetPv.vt = PROPVARIANT.VT_LPWSTR;
                targetPv.p1 = targetPtr;
            }
            store.SetValue(ref targetKey, ref targetPv);

            PROPERTYKEY enableKey = PropertyKeys.PKEY_AudioEndpoint_ListenToThisDevice;
            PROPVARIANT enablePv = default;
            enablePv.vt = PROPVARIANT.VT_BOOL;
            // VT_BOOL is VARIANT_BOOL: -1 (0xFFFF) for TRUE, 0 for FALSE. Stored in p1's low word.
            enablePv.p1 = enable ? new IntPtr(unchecked(0xFFFF)) : IntPtr.Zero;
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
            Safe.Release(store);
        }

        RefreshListenStateFromStore();
    }

    private void WriteListenBool(PROPERTYKEY key, bool value)
    {
        IPropertyStore? store = null;
        try
        {
            _device.OpenPropertyStore(Stgm.Write, out store);
            PROPVARIANT pv = default;
            pv.vt = PROPVARIANT.VT_BOOL;
            pv.p1 = value ? new IntPtr(unchecked(0xFFFF)) : IntPtr.Zero;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.WriteListenBool({FriendlyName}, pid={key.pid}): {ex.Message}");
        }
        finally
        {
            Safe.Release(store);
        }
    }

    // Writes PKEY_AudioEndpoint_Disable_SysFx as VT_UI4 DWORD. disabled=false -> 0 (engine
    // applies enhancements + the listen monitor); disabled=true -> 1 (engine bypasses both).
    // Callers force this to false when enabling the listen feature so a stale "Disable all
    // enhancements" checkbox can't silently no-op the listen monitor. Best-effort - on drivers
    // that don't expose an enhancements pipeline the write is harmless.
    private void WriteDisableSysFx(bool disabled)
    {
        IPropertyStore? store = null;
        try
        {
            _device.OpenPropertyStore(Stgm.Write, out store);
            PROPVARIANT pv = default;
            pv.vt = PROPVARIANT.VT_UI4;
            pv.p1 = new IntPtr(disabled ? 1 : 0);
            PROPERTYKEY key = PropertyKeys.PKEY_AudioEndpoint_Disable_SysFx;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.WriteDisableSysFx({FriendlyName}, disabled={disabled}): {ex.Message}");
        }
        finally
        {
            Safe.Release(store);
        }
    }

    /// <summary>
    /// Re-read PKEY_AudioEndpoint_AllowExclusiveControl from the endpoint property store and
    /// push the result through <see cref="IsExclusiveModeAllowed"/>. Invoked by the manager
    /// when OnPropertyValueChanged fires for this PKEY (e.g. mmsys.cpl checkbox edits).
    /// </summary>
    internal void RefreshAllowExclusiveControlFromStore()
    {
        if (_disposed) return;
        IsExclusiveModeAllowed = ReadAllowExclusiveControlFromStore(_device);
    }

    private void WriteAllowExclusiveControl(bool allowed)
    {
        IPropertyStore? store = null;
        try
        {
            _device.OpenPropertyStore(Stgm.Write, out store);

            // VT_UI4 lives in the low 32 bits of p1; high bits are ignored. mmsys.cpl writes
            // REG_DWORD 1 / 0 - matching that keeps the value layout identical for round-trips.
            PROPVARIANT pv = default;
            pv.vt = PROPVARIANT.VT_UI4;
            pv.p1 = new IntPtr(allowed ? 1 : 0);

            // pid 3 - the master "Allow applications to take exclusive control" bit.
            PROPERTYKEY allowKey = PropertyKeys.PKEY_AudioEndpoint_AllowExclusiveControl;
            store.SetValue(ref allowKey, ref pv);

            // pid 4 - the sub-checkbox "Give exclusive mode applications priority". Yoked to
            // the master so one button drives both, matching mmsys.cpl's enabled-when-allowed
            // affordance. Same VT_UI4 / DWORD encoding.
            PROPERTYKEY priorityKey = PropertyKeys.PKEY_AudioEndpoint_ExclusiveModeAppsPriority;
            store.SetValue(ref priorityKey, ref pv);

            store.Commit();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.WriteAllowExclusiveControl({FriendlyName}, allowed={allowed}): {ex.Message}");
        }
        finally
        {
            Safe.Release(store);
        }
    }

    private static bool ReadAllowExclusiveControlFromStore(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(Stgm.Read, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_AudioEndpoint_AllowExclusiveControl;
            store.GetValue(ref key, out PROPVARIANT pv);
            try
            {
                // VT_EMPTY (never toggled) means the OS default "allowed" applies. VT_UI4 is the
                // canonical form mmsys.cpl writes; VT_I4 / VT_BOOL accepted as defensive fallbacks
                // in case a driver or older build encodes it differently.
                return pv.vt switch
                {
                    PROPVARIANT.VT_EMPTY => true,
                    PROPVARIANT.VT_UI4 => pv.GetUInt32() != 0,
                    PROPVARIANT.VT_I4 => pv.p1.ToInt64() != 0,
                    PROPVARIANT.VT_BOOL => ((short)(pv.p1.ToInt64() & 0xFFFF)) != 0,
                    _ => true,
                };
            }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch { return true; }
        finally { Safe.Release(store); }
    }

    private static (bool Enabled, string? TargetDeviceID) ReadListenStateFromStore(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(Stgm.Read, out store);

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

            PROPERTYKEY targetKey = PropertyKeys.PKEY_AudioEndpoint_ListenTargetDeviceID;
            store.GetValue(ref targetKey, out PROPVARIANT targetPv);
            string? target;
            try { target = targetPv.GetString(); }
            finally { Ole32.PropVariantClear(ref targetPv); }

            return (enabled, target);
        }
        catch { return (false, null); }
        finally { Safe.Release(store); }
    }

    /// <summary>
    /// Attempts the IAudioEndpointVolume / IAudioMeterInformation / IAudioSessionManager2 activation
    /// chain. All three live behind the same WASAPI activation gate, so a single failure path covers
    /// disconnect / disable / unplug. Idempotent - skips fields that are already populated.
    /// </summary>
    private void TryActivateProxies()
    {
        if (_endpointVolume != null)
        {
            RefreshEndpointVolumeState();
            return;
        }

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

            // Notify bindings that volume/mute now have real values - the wrapper may have lived as
            // an inactive shell up to this point and the bound UI is showing the 0/false defaults.
            RefreshEndpointVolumeState(forceNotify: true);

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

    private bool RefreshEndpointVolumeState(bool forceNotify = false)
    {
        if (_disposed) return false;

        IAudioEndpointVolume? endpointVolume = _endpointVolume;
        if (endpointVolume == null) return false;

        try
        {
            endpointVolume.GetMasterVolumeLevelScalar(out float volume);
            endpointVolume.GetMute(out bool muted);
            if (float.IsNaN(volume) || float.IsInfinity(volume)) return false;

            float clamped = Math.Clamp(volume, 0f, 1f);
            bool volumeChanged = forceNotify || Math.Abs(clamped - _volume) >= 0.0005f;
            bool muteChanged = forceNotify || muted != _isMuted;

            _volume = clamped;
            _isMuted = muted;

            if (volumeChanged) OnPropertyChanged(nameof(Volume));
            if (muteChanged) OnPropertyChanged(nameof(IsMuted));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-runs the endpoint activation chain when the OS reports the device transitioned to
    /// Active. If the proxies are still wired, refreshes the cached volume/mute values from the
    /// live endpoint. Called by AudioDeviceManager from its OnDeviceStateChanged path when
    /// newState includes the Active bit.
    /// </summary>
    internal void UpgradeFromActiveState() => TryActivateProxies();

    /// <summary>
    /// Drops Active-only endpoint proxies after the OS reports this wrapper leaving Active. The next
    /// Active transition must activate fresh proxies so endpoint-volume callbacks re-subscribe
    /// instead of staying attached to an invalidated audio-engine instance.
    /// </summary>
    internal void DowngradeFromActiveState()
    {
        if (_disposed) return;
        ReleaseEndpointProxies();
    }

    private void ReleaseEndpointProxies()
    {
        _volumeWrite.Drop();

        IAudioEndpointVolume? endpointVolume = _endpointVolume;
        IAudioMeterInformation? endpointMeter = _endpointMeter;
        IAudioSessionManager2? sessionManager = _sessionManager;
        EndpointVolumeBridge? volumeBridge = _volumeBridge;
        SessionNotificationBridge? sessionBridge = _sessionBridge;

        _endpointVolume = null;
        _endpointMeter = null;
        _sessionManager = null;
        _volumeBridge = null;
        _sessionBridge = null;

        if (endpointVolume != null && volumeBridge != null)
            try { endpointVolume.UnregisterControlChangeNotify(volumeBridge); } catch { }

        if (sessionManager != null && sessionBridge != null)
            try { sessionManager.UnregisterSessionNotification(sessionBridge); } catch { }

        Safe.Release(endpointVolume);
        Safe.Release(endpointMeter);
        Safe.Release(sessionManager);
    }

    /// <summary>
    /// Plays a short PCM wav through this endpoint via WASAPI shared mode. Used by the volume-
    /// change feedback ding so the sound comes out of the device the user just adjusted instead
    /// of the system default. No-op on capture endpoints (microphones don't render audio) and on
    /// devices not currently in the Active state.
    /// We pass the endpoint id (not _device) because the playback runs on a threadpool worker and
    /// the IMMDevice RCW is bound to the WPF UI-thread STA - re-acquiring on the worker is the
    /// supported way to cross apartments.
    /// </summary>
    internal void PlayChangeFeedback(byte[] wavBytes)
    {
        if (_disposed || DataFlow != EDataFlow.eRender || !IsActive || string.IsNullOrEmpty(Id)) return;
        EndpointSoundPlayback.PlayAsync(Id, wavBytes);
    }

    /// <summary>
    /// Bg-thread half of the sample tick. Reads the endpoint per-channel peaks via COM and stashes
    /// them on the <see cref="MeterLerp"/> via <see cref="MeterLerp.WriteRawPeaks"/>, then cascades
    /// into every group so per-session raw peaks are filled in parallel - all off the UI thread.
    /// The groups list is snapshotted under try/catch since UI-thread mutations could otherwise
    /// tear the enumerator; a torn frame just means we miss one tick for the affected device.
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
            _meterLerp.PinRawPeaksToSilence();
        }
        else
        {
            try
            {
                MeterReader.ReadStereoPeaks(meter, unified, biasMultiplier, out float minPeak, out float maxPeak);

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
                    // Windows-confirmed latch - ignore the stale COM value and force silence so
                    // the lerp decays. Stays in this branch until a genuinely different pair lands.
                    _meterLerp.PinRawPeaksToSilence();
                }
                else
                    _meterLerp.WriteRawPeaks(minPeak, maxPeak);
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
    /// UI-thread half of the sample tick. Hands off to <see cref="MeterLerp.OnNewSample"/> which
    /// snapshots the current display values as the new lerp origins and arms the step counter at
    /// the supplied span (typically Fps / SampleRate). Forwards the same call into every
    /// <see cref="AudioAppGroup"/> so per-app session sliders interpolate too.
    /// </summary>
    internal void OnNewSample(int interpolationSteps)
    {
        if (_disposed) return;

        _meterLerp.OnNewSample(interpolationSteps);

        for (int i = _groups.Count - 1; i >= 0; i--) _groups[i].OnNewSample(interpolationSteps);
    }

    // Stuck-meter watchdog callback. Fires on a threadpool worker after MeterStaleWatchdogMs of
    // bit-exact same-value reads. Just flips the latched flag - the next bg sample sees it and
    // routes through PinRawPeaksToSilence instead of writing the stale value back to the lerp.
    private void OnStuckMeterWatchdog(object? _)
    {
        if (_disposed) return;
        _meterIsLatched = true;
    }

    /// <summary>
    /// Render-timer callback. Advances the lerp step counter and fires PropertyChanged on actual
    /// change so both bound meter borders redraw every frame. UI-thread.
    /// <paramref name="maxStep"/> is the user-configurable rate-limit ceiling sourced from
    /// AppSettings.MeterPeakChangeCeiling, snapshotted once per render tick by the manager and
    /// applied inside the lerp so it advances on every frame regardless of binding state.
    /// </summary>
    internal void OnRenderTick(float maxStep)
    {
        if (_disposed) return;

        _meterLerp.OnRenderTick(maxStep, out bool minChanged, out bool maxChanged);
        if (minChanged) OnPropertyChanged(nameof(PeakValueMin));
        if (maxChanged) OnPropertyChanged(nameof(PeakValueMax));

        for (int i = _groups.Count - 1; i >= 0; i--) _groups[i].OnRenderTick(maxStep);
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
            Safe.Release(enumerator);
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
            Safe.Release(ctrl);
            return;
        }

        // Dedup: register-then-enumerate in the ctor opens a window where the same COM session is
        // both notified and enumerated. Drop the duplicate wrapper rather than letting two run.
        string key = session.SessionInstanceID;
        if (key.Length > 0 && _sessionsBySessionInstanceID.ContainsKey(key))
        {
            Safe.Dispose(session);
            return;
        }

        session.Disconnected += OnSessionDisconnected;
        session.StateChanged += OnSessionStateChanged;
        if (key.Length > 0) _sessionsBySessionInstanceID[key] = session;

        // Route into the matching group by AppID, or create a new group when this is the first
        // session for the app. Linear scan is fine - typical session counts are well under a dozen.
        AudioAppGroup? group = null;
        for (int i = 0; i < _groups.Count; i++)
            if (_groups[i].AppID == session.AppID) { group = _groups[i]; break; }

        if (group == null)
        {
            // Populate the group with its first session BEFORE publishing it to the observable
            // collection. _groups.Add fires CollectionChanged synchronously, which the flyout uses
            // to rebuild its visible list - if the group is published while still empty, any
            // "skip empty groups" filter on the consumer side would hide the new app entirely
            // until the next unrelated _groups change forced another rebuild.
            group = new AudioAppGroup(session.AppID, _dispatcher);
            group.Empty += OnGroupEmpty;
            group.AddSession(session);
            _groups.Add(group);
        }
        else
            group.AddSession(session);

        // A newly-added Active session wakes the capture engine; recompute so the bound UI flips
        // off MICROPHONE_SLEEP without waiting for the next state-change event.
        RecomputeCaptureSleepingState();

        // Exclusive mode and shared-mode sessions can't coexist on the same endpoint - the audio
        // engine refuses to create shared-mode sessions while exclusive holds. So the mere arrival
        // of any new session here means whatever held exclusive has released; clear the indicator.
        // Covers the common "exclusive app quit; shared playback resumed" recovery case.
        if (_isExclusiveControlHeld) IsExclusiveControlHeld = false;
    }

    private void OnSessionDisconnected(AudioSession session)
    {
        // ExclusiveModeOverride means another app called IAudioClient.Initialize in exclusive
        // mode and the audio engine kicked all shared-mode sessions off this endpoint. That's
        // the canonical signal for "exclusive control is now held"; flip the lock indicator
        // before we drop the session wrapper. Other reasons (device removal, format change,
        // logoff, our own process-exit synthesis) are just lifecycle and don't imply exclusive.
        if (session.LastDisconnectReason == AudioSessionDisconnectReason.ExclusiveModeOverride) IsExclusiveControlHeld = true;
        RemoveSession(session);
    }

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
            if (_groups[i].State == AudioSessionState.Active) return true;
        return false;
    }

    private void RemoveSession(AudioSession session)
    {
        string key = session.SessionInstanceID;
        if (key.Length > 0) _sessionsBySessionInstanceID.Remove(key);

        // Find the owning group by walking the list. Sessions can only belong to one group.
        for (int i = 0; i < _groups.Count; i++)
        {
            AudioAppGroup g = _groups[i];
            if (!g.Sessions.Contains(session)) continue;

            session.Disconnected -= OnSessionDisconnected;
            session.StateChanged -= OnSessionStateChanged;
            g.RemoveSession(session);
            Safe.Dispose(session);
            // Losing this session may have been the last active one; refresh the sleep flag.
            RecomputeCaptureSleepingState();
            return;
        }
    }

    private void OnGroupEmpty(AudioAppGroup group)
    {
        group.Empty -= OnGroupEmpty;
        _groups.Remove(group);
        Safe.Dispose(group);
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
            _device.OpenPropertyStore(Stgm.Write, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;

            PROPVARIANT pv = default;
            if (trimmed == null)
                pv.vt = PROPVARIANT.VT_EMPTY;
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
            Safe.Release(store);
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

    /// <summary>
    /// Re-read PKEY_AudioEngine_DeviceFormat and update <see cref="DefaultFormat"/>. Invoked by
    /// the manager when OnPropertyValueChanged fires for the format pid on this device id, so the
    /// flyout's compact format readout tracks live changes from the Sound Control Panel.
    /// </summary>
    internal void RefreshDefaultFormatFromStore()
    {
        if (_disposed) return;
        DefaultFormat = ResolveDefaultFormat(_device);
    }

    /// <summary>
    /// Returns the (channels, bit depth, sample rate) combinations to show in the default-format
    /// picker. Source is the audio device's advertised KS pin data ranges - the same list
    /// mmsys.cpl's Advanced > Default Format dropdown reads from. Returns an empty list when
    /// the device has no queryable KS topology (rare; pure virtual / software-only endpoints).
    /// </summary>
    internal List<(int Channels, int Bits, int SampleRate)> EnumerateSupportedFormats()
    {
        WPFLog.Log($"AudioDevice.EnumerateSupportedFormats({FriendlyName}): entry, _disposed={_disposed} IsActive={IsActive} State={State}");
        List<(int, int, int)> empty = new();
        if (_disposed || !IsActive) return empty;

        try
        {
            ushort currentChannels = 2;
            byte[]? currentBlob = ReadCurrentFormatBlob();
            if (currentBlob != null && currentBlob.Length >= 18)
                currentChannels = BitConverter.ToUInt16(currentBlob, 2);

            // Channel-set candidates: the canonical mmsys.cpl layouts (mono / stereo / quad /
            // 5.1 / 7.1) unioned with whatever the endpoint currently reports, so a 3ch / 5ch
            // device still surfaces its current format as a selectable row.
            SortedSet<int> channelSet = new() { 1, 2, 4, 6, 8, currentChannels };

            // mmsys.cpl never offers 32-bit as a user-selectable bit depth (the audio engine's
            // 32-bit-float internal format doesn't surface in the picker), so 32-bit is omitted.
            // 24-bit is restricted to >= 44100 Hz - mmsys.cpl never shows 24-bit at
            // telephone/radio rates either.
            int[] rates16 = { 8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000 };
            int[] rates24 = { 44100, 48000, 88200, 96000, 176400, 192000 };

            List<(int, int, int)>? fromKs = QueryKsAudioDataRanges(channelSet, rates16, rates24);
            return fromKs ?? empty;
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.EnumerateSupportedFormats({FriendlyName}): {ex.Message}");
            return empty;
        }
    }

    // Replicates the algorithm mmsys.cpl uses for its Advanced > Default Format dropdown
    // (verified from a decompile of mmsys.cpl's CEndpointFormatChanger::IsPCMFormatSupported
    // and CPageFormat::AddStdFormatsToFormatCombo):
    //
    //   1. IMMDevice -> IDeviceTopology -> IConnector(0) -> QI(IPart)
    //   2. IPart::Activate(CLSCTX_INPROC_SERVER, IID_IKsFormatSupport, ...) -> IKsFormatSupport
    //   3. For each candidate (channels, validBits, containerBits, rate):
    //        a. Build WAVEFORMATEXTENSIBLE (40 bytes)
    //        b. Wrap in KSDATAFORMAT_WAVEFORMATEX (64 KSDATAFORMAT header + 40 WFX = 104 bytes)
    //           with MajorFormat=KSDATAFORMAT_TYPE_AUDIO, SubFormat=KSDATAFORMAT_SUBTYPE_PCM,
    //           Specifier=KSDATAFORMAT_SPECIFIER_WAVEFORMATEX
    //        c. IKsFormatSupport::IsFormatSupported(pKsFormat, 104, &supported)
    //        d. If supported, add (channels, validBits, rate) to the result set
    //
    // The probe set comes from rates16 / rates24 and channelCandidates. mmsys.cpl uses 39
    // hardcoded entries; ours is a tighter list curated to match what the dropdown surfaces.
    //
    // IKsControl and KSPROPERTY_PIN_DATARANGES were tried earlier and abandoned: every audio
    // driver we tested returns E_NOINTERFACE for IID_IKsControl on every IPart in its topology,
    // and mmsys.cpl confirmedly doesn't use that path.
    //
    // All COM-pointer outputs go through out IntPtr + explicit Marshal.QueryInterface / Release,
    // matching NAudio's pattern - the .NET classic-RCW marshaller mis-handles QI for interfaces
    // returned across topology boundaries.
    // Replicates the algorithm mmsys.cpl uses for its Advanced > Default Format dropdown.
    // The decompile of mmsys.cpl shows this exact chain, and a self-contained test harness
    // confirmed it produces the expected format list against the Realtek Digital Output
    // endpoint where every public-topology approach (IKsControl, IKsFormatSupport on public
    // IPart, KSPROPERTY_PIN_DATARANGES via DeviceIoControl) returned E_NOINTERFACE / empty.
    //
    //   1. IMMDevice::Activate(IID_AudioEnginePartFilter, CLSCTX_INPROC_SERVER, NULL, &filter)
    //      Private Microsoft IID 2b0711de-dab7-4610-a16f-d3383749b220. Returns a filter that
    //      can hand back IPart objects from the audio engine's internal topology (which the
    //      public IDeviceTopology::GetConnector never exposes).
    //
    //   2. filter->vtable[3](&ksDataFormat=64B, 64, NULL, &enumerator)
    //      Pass a 64-byte KSDATAFORMAT header (TYPE_AUDIO / SUBTYPE_PCM / SPECIFIER_WAVEFORMATEX,
    //      no payload). Get back an IPart enumerator scoped to that data range.
    //
    //   3. enumerator->vtable[3](&count) / vtable[4](i, &part)
    //      Iterate the enumerator. Each part is a regular IPart in the engine's audio topology.
    //
    //   4. part->Activate(CLSCTX_INPROC_SERVER, IID_IKsFormatSupport, &fs)
    //      Activate IKsFormatSupport on the part. This SUCCEEDS here even when it returns
    //      E_NOINTERFACE on every part of the public topology - the engine topology's parts
    //      carry it.
    //
    //   5. fs->IsFormatSupported(KSDATAFORMAT_WAVEFORMATEX{104}, 104, &supported)
    //      Per-candidate probe, exactly what mmsys.cpl does in
    //      CEndpointFormatChanger::IsPCMFormatSupported.
    private List<(int, int, int)>? QueryKsAudioDataRanges(
        SortedSet<int> channelCandidates, int[] rates16, int[] rates24)
    {
        IntPtr filterPtr = IntPtr.Zero;
        IntPtr enumeratorPtr = IntPtr.Zero;
        IntPtr ksDataPtr = IntPtr.Zero;

        try
        {
            int hr = _device.Activate(KsConstants.IID_AudioEnginePartFilter,
                ClsCtx.INPROC_SERVER, IntPtr.Zero, out object? filterObj);
            WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): Activate(IID_AudioEnginePartFilter) hr=0x{hr:X8}");
            if (hr < 0 || filterObj == null) return null;

            filterPtr = Marshal.GetIUnknownForObject(filterObj);
            Marshal.FinalReleaseComObject(filterObj);  // release the temporary RCW; filterPtr keeps the COM ref

            // Build the 64-byte KSDATAFORMAT header (audio PCM via WAVEFORMATEX specifier).
            ksDataPtr = Marshal.AllocHGlobal(64);
            for (int i = 0; i < 64; i++) Marshal.WriteByte(ksDataPtr, i, 0);
            Marshal.WriteInt32(ksDataPtr, 0, 64);   // FormatSize
            Marshal.Copy(KsConstants.KSDATAFORMAT_TYPE_AUDIO.ToByteArray(), 0, IntPtr.Add(ksDataPtr, 16), 16);
            Marshal.Copy(PropertyKeys.KSDATAFORMAT_SUBTYPE_PCM.ToByteArray(), 0, IntPtr.Add(ksDataPtr, 32), 16);
            Marshal.Copy(KsConstants.KSDATAFORMAT_SPECIFIER_WAVEFORMATEX.ToByteArray(), 0, IntPtr.Add(ksDataPtr, 48), 16);

            // Call filter->vtable[3] (the "filter parts by KSDATAFORMAT" method).
            int fhr = CallVtable3_Filter(filterPtr, ksDataPtr, 64, out enumeratorPtr);
            WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): filter->vtable[3] hr=0x{fhr:X8} enum=0x{enumeratorPtr.ToInt64():X}");
            if (fhr < 0 || enumeratorPtr == IntPtr.Zero) return null;

            int chr = CallVtable3_GetCount(enumeratorPtr, out uint count);
            WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): enum->GetCount hr=0x{chr:X8} count={count}");
            if (chr < 0 || count == 0) return null;

            IKsFormatSupport? formatSupport = null;
            IntPtr formatSupportPtr = IntPtr.Zero;
            try
            {
                for (uint i = 0; i < count && formatSupport == null; i++)
                {
                    int ihr = CallVtable4_GetItem(enumeratorPtr, i, out IntPtr itemPtr);
                    if (ihr < 0 || itemPtr == IntPtr.Zero) continue;
                    try
                    {
                        Guid iidPart = typeof(IPart).GUID;
                        int qhr = Marshal.QueryInterface(itemPtr, in iidPart, out IntPtr partPtr);
                        if (qhr < 0 || partPtr == IntPtr.Zero) continue;
                        try
                        {
                            IPart part = (IPart)Marshal.GetObjectForIUnknown(partPtr);
                            Guid iidFs = typeof(IKsFormatSupport).GUID;
                            int ahr = part.Activate(ClsCtx.INPROC_SERVER, ref iidFs, out IntPtr fsPtr);
                            if (ahr >= 0 && fsPtr != IntPtr.Zero)
                            {
                                WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): part[{i}] -> IKsFormatSupport");
                                formatSupportPtr = fsPtr;
                                formatSupport = (IKsFormatSupport)Marshal.GetObjectForIUnknown(fsPtr);
                                break;
                            }
                        }
                        finally { Marshal.Release(partPtr); }
                    }
                    finally { Marshal.Release(itemPtr); }
                }

                if (formatSupport == null) return null;

                SortedSet<(int, int, int)> accepted = new();
                int probed = 0;
                int supported = 0;

                foreach (int channels in channelCandidates)
                {
                    if (channels < 1) continue;
                    foreach (int rate in rates16)
                    {
                        probed++;
                        if (ProbeFormat(formatSupport, (ushort)channels, 16, 16, (uint)rate))
                        {
                            supported++;
                            accepted.Add((channels, 16, rate));
                        }
                    }
                    foreach (int rate in rates24)
                    {
                        probed++;
                        // 24-bit ships as 24-in-32 on most render endpoints (e.g. Realtek S/PDIF)
                        // and as packed 24-in-24 on most USB audio drivers (e.g. AT2020USB-X mic).
                        // Accept either - the union matches mmsys.cpl's behavior, verified against
                        // both device types via the FormatProbe test harness.
                        bool ok = ProbeFormat(formatSupport, (ushort)channels, 24, 32, (uint)rate)
                               || ProbeFormat(formatSupport, (ushort)channels, 24, 24, (uint)rate);
                        if (ok)
                        {
                            supported++;
                            accepted.Add((channels, 24, rate));
                        }
                    }
                }

                WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): probed={probed} supported={supported} accepted={accepted.Count}");
                return new List<(int, int, int)>(accepted);
            }
            finally
            {
                if (formatSupportPtr != IntPtr.Zero) Marshal.Release(formatSupportPtr);
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AudioDevice.QueryKsAudioDataRanges({FriendlyName}): exception {ex.GetType().Name} {ex.Message}");
            return null;
        }
        finally
        {
            if (ksDataPtr != IntPtr.Zero) Marshal.FreeHGlobal(ksDataPtr);
            if (enumeratorPtr != IntPtr.Zero) Marshal.Release(enumeratorPtr);
            if (filterPtr != IntPtr.Zero) Marshal.Release(filterPtr);
        }
    }

    // Raw vtable dispatchers for the private IID_AudioEnginePartFilter chain. We don't have a
    // type definition for these interfaces; the call signatures are reverse-engineered from the
    // mmsys.cpl decompile and validated against the test harness.

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FilterMethod3Fn(IntPtr thisPtr, IntPtr ksData, uint cbKsData, IntPtr unused, out IntPtr outEnumerator);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumGetCountFn(IntPtr thisPtr, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumGetItemFn(IntPtr thisPtr, uint index, out IntPtr outItem);

    private static int CallVtable3_Filter(IntPtr objPtr, IntPtr ksData, uint cbKsData, out IntPtr outEnumerator)
    {
        IntPtr vtable = Marshal.ReadIntPtr(objPtr);
        IntPtr slot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        FilterMethod3Fn fn = Marshal.GetDelegateForFunctionPointer<FilterMethod3Fn>(slot);
        return fn(objPtr, ksData, cbKsData, IntPtr.Zero, out outEnumerator);
    }

    private static int CallVtable3_GetCount(IntPtr objPtr, out uint count)
    {
        IntPtr vtable = Marshal.ReadIntPtr(objPtr);
        IntPtr slot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        EnumGetCountFn fn = Marshal.GetDelegateForFunctionPointer<EnumGetCountFn>(slot);
        return fn(objPtr, out count);
    }

    private static int CallVtable4_GetItem(IntPtr objPtr, uint index, out IntPtr outItem)
    {
        IntPtr vtable = Marshal.ReadIntPtr(objPtr);
        IntPtr slot = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        EnumGetItemFn fn = Marshal.GetDelegateForFunctionPointer<EnumGetItemFn>(slot);
        return fn(objPtr, index, out outItem);
    }

    // Builds a 104-byte KSDATAFORMAT_WAVEFORMATEX (64-byte header + 40-byte WAVEFORMATEXTENSIBLE)
    // and calls IKsFormatSupport::IsFormatSupported. Layout:
    //   [0..3]   FormatSize         = 104
    //   [4..7]   Flags              = 0
    //   [8..11]  SampleSize         = 0
    //   [12..15] Reserved           = 0
    //   [16..31] MajorFormat        = KSDATAFORMAT_TYPE_AUDIO
    //   [32..47] SubFormat          = KSDATAFORMAT_SUBTYPE_PCM
    //   [48..63] Specifier          = KSDATAFORMAT_SPECIFIER_WAVEFORMATEX
    //   [64..103] WAVEFORMATEXTENSIBLE blob (BuildFormatBlob output, 40 bytes)
    private static bool ProbeFormat(
        IKsFormatSupport formatSupport,
        ushort channels, ushort validBits, ushort containerBits, uint sampleRate)
    {
        const int KsDataFormatHeaderSize = 64;
        const int WfxBlobSize = 40;
        const int TotalSize = KsDataFormatHeaderSize + WfxBlobSize; // 104

        byte[] wfxBlob = BuildFormatBlob(channels, validBits, containerBits, sampleRate);
        if (wfxBlob.Length != WfxBlobSize) return false;

        IntPtr p = Marshal.AllocHGlobal(TotalSize);
        try
        {
            // Zero the header so SampleSize/Reserved/Flags are clean.
            for (int i = 0; i < TotalSize; i++) Marshal.WriteByte(p, i, 0);

            Marshal.WriteInt32(p, 0, TotalSize);                  // FormatSize
            // Flags / SampleSize / Reserved already zero from the wipe above.

            byte[] majorFormat = KsConstants.KSDATAFORMAT_TYPE_AUDIO.ToByteArray();
            byte[] subFormat = PropertyKeys.KSDATAFORMAT_SUBTYPE_PCM.ToByteArray();
            byte[] specifier = KsConstants.KSDATAFORMAT_SPECIFIER_WAVEFORMATEX.ToByteArray();
            Marshal.Copy(majorFormat, 0, IntPtr.Add(p, 16), 16);
            Marshal.Copy(subFormat, 0, IntPtr.Add(p, 32), 16);
            Marshal.Copy(specifier, 0, IntPtr.Add(p, 48), 16);
            Marshal.Copy(wfxBlob, 0, IntPtr.Add(p, KsDataFormatHeaderSize), WfxBlobSize);

            int hr = formatSupport.IsFormatSupported(p, TotalSize, out bool supported);
            return hr >= 0 && supported;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// Writes a new default mix format to this endpoint via IPolicyConfig::SetDeviceFormat,
    /// preserving the endpoint's current channel layout / SubFormat from PKEY_AudioEngine_DeviceFormat.
    /// Off-loaded to the threadpool through the shared single-flight gate (same as SetAsDefault /
    /// SetEnabled) - the audio service blocks for hundreds of ms here while it tears the engine
    /// down and back up at the new rate. The DefaultFormat property updates when the resulting
    /// OnPropertyValueChanged callback fires for the format pid.
    /// </summary>
    internal void SetDeviceFormat(int channels, int bits, int sampleRate)
    {
        if (_disposed || string.IsNullOrEmpty(Id)) return;

        // 24-bit is written as 24-in-32 (containerBits=32, validBits=24) - the form every modern
        // driver expects. 16-bit and any other size are written container == valid.
        ushort containerBits = bits == 24 ? (ushort)32 : (ushort)bits;
        byte[] blob = BuildFormatBlob((ushort)channels, (ushort)bits, containerBits, (uint)sampleRate);

        string id = Id;
        string friendlyName = FriendlyName;

        RunPolicyConfigCall(client =>
        {
            IntPtr pBlob = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, pBlob, blob.Length);
                int hr = client.SetDeviceFormat(id, pBlob, pBlob);
                if (hr < 0) WPFLog.Log($"AudioDevice.SetDeviceFormat({friendlyName}, {channels}ch/{bits}-bit/{sampleRate}Hz): hr=0x{hr:X8}");
            }
            finally { Marshal.FreeHGlobal(pBlob); }
        }, $"SetDeviceFormat({friendlyName}, {channels}ch/{bits}-bit/{sampleRate}Hz)");
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
            device.OpenPropertyStore(Stgm.Read, out store);

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
            Safe.Release(store);
        }
    }

    // Primary signal: the PnP bus enumerator the endpoint's underlying device sits on. Bluetooth
    // Classic devices (A2DP / HFP) report "BTHENUM" verbatim; we observed this on the Sony
    // WH-1000XM4 and other paired BT headsets via the audio endpoint property store. Friendly
    // names alone are unreliable - Win11 strips the "Bluetooth" prefix many drivers used to add,
    // so a device can read as plain "Headphones (WH-1000XM4)" with no protocol hint.
    // Fallbacks (name substrings) cover the rare driver that doesn't surface EnumeratorName
    // through the endpoint property store.
    private static readonly string[] BluetoothNameTokens = ["Bluetooth", "Hands-Free", "A2DP"];

    private static bool DetectIsBluetooth(string enumerator, string friendlyName,
        string deviceDescription, string interfaceFriendlyName)
    {
        WPFLog.LogDebug($"AudioDevice.DetectIsBluetooth: friendly='{friendlyName}' enumerator='{(enumerator.Length == 0 ? "<empty>" : enumerator)}'");

        if (enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (string token in BluetoothNameTokens)
        {
            if (friendlyName.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
            if (deviceDescription.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
            if (interfaceFriendlyName.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Reads PKEY_Device_EnumeratorName off the audio endpoint property store. Returns null on
    // missing property / wrong type / COM exception so callers can chain to a fallback signal.
    private static string? ReadEnumeratorName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(Stgm.Read, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_Device_EnumeratorName;
            store.GetValue(ref key, out PROPVARIANT pv);
            try { return pv.GetString(); }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { Safe.Release(store); }
    }

    // Reads PKEY_Device_ContainerId off the audio endpoint property store. Returns null on
    // missing property / wrong type / COM exception. The audio endpoint inherits the container
    // id from its underlying PnP device, so for a Bluetooth headset this matches the GUID that
    // BluetoothBatteryMonitor reads from the corresponding devnode.
    private static Guid? ReadContainerId(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(Stgm.Read, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_Device_ContainerId;
            store.GetValue(ref key, out PROPVARIANT pv);
            try { return pv.GetGuid(); }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { Safe.Release(store); }
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

    // Reads PKEY_AudioEngine_DeviceFormat as a raw byte[] - the WAVEFORMATEX(TENSIBLE) blob
    // backing both the format readout and the format-picker probe. Returns null on disabled /
    // unplugged endpoints (no property or empty blob) and on COM exceptions.
    private byte[]? ReadCurrentFormatBlob()
    {
        IPropertyStore? store = null;
        try
        {
            _device.OpenPropertyStore(Stgm.Read, out store);
            PROPERTYKEY key = PropertyKeys.PKEY_AudioEngine_DeviceFormat;
            store.GetValue(ref key, out PROPVARIANT pv);
            try { return pv.GetBlobBytes(); }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch
        {
            return null;
        }
        finally
        {
            Safe.Release(store);
        }
    }

    // WAVE_FORMAT_EXTENSIBLE: the wFormatTag value used by EXTENSIBLE-form WAVEFORMATEX blobs
    // (channels > 2, channel masks present, SubFormat GUID). Same value referenced from BuildFormatBlob
    // and ParseFormatBlob below.
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    // Synthesizes a 40-byte WAVEFORMATEXTENSIBLE byte image for the given (channels, valid bits,
    // container bits, rate). SubFormat is KSDATAFORMAT_SUBTYPE_PCM; mask is the standard
    // KSAUDIO_SPEAKER_* layout for the channel count. 24-bit is conventionally encoded as
    // 24-in-32 (containerBits=32, validBits=24).
    private static byte[] BuildFormatBlob(ushort channels, ushort validBits, ushort containerBits, uint sampleRate)
    {
        const ushort EXTENSIBLE_CB_SIZE = 22;

        ushort blockAlign = (ushort)(channels * (containerBits / 8));
        uint avgBytesPerSec = sampleRate * blockAlign;
        uint mask = channels switch
        {
            1 => 0x4u,                              // KSAUDIO_SPEAKER_MONO (FRONT_CENTER)
            2 => 0x3u,                              // KSAUDIO_SPEAKER_STEREO (FL | FR)
            4 => 0x33u,                             // KSAUDIO_SPEAKER_QUAD
            6 => 0x3Fu,                             // KSAUDIO_SPEAKER_5POINT1
            8 => 0xFFu,                             // KSAUDIO_SPEAKER_7POINT1
            _ => channels >= 32 ? 0u : (1u << channels) - 1,
        };

        byte[] ext = new byte[40];
        BitConverter.GetBytes(WAVE_FORMAT_EXTENSIBLE).CopyTo(ext, 0);
        BitConverter.GetBytes(channels).CopyTo(ext, 2);
        BitConverter.GetBytes(sampleRate).CopyTo(ext, 4);
        BitConverter.GetBytes(avgBytesPerSec).CopyTo(ext, 8);
        BitConverter.GetBytes(blockAlign).CopyTo(ext, 12);
        BitConverter.GetBytes(containerBits).CopyTo(ext, 14);
        BitConverter.GetBytes(EXTENSIBLE_CB_SIZE).CopyTo(ext, 16);
        BitConverter.GetBytes(validBits).CopyTo(ext, 18);
        BitConverter.GetBytes(mask).CopyTo(ext, 20);
        PropertyKeys.KSDATAFORMAT_SUBTYPE_PCM.ToByteArray().CopyTo(ext, 24);
        return ext;
    }

    // Reads PKEY_AudioEngine_DeviceFormat and reduces the WAVEFORMATEX(TENSIBLE) blob to the
    // three numbers Sound Control Panel surfaces: channel count, bit depth, sample rate. Returns
    // null on disabled / unplugged endpoints (no property or empty blob), on parse failures, and
    // on any COM exception - bound TextBlocks just render empty in those cases.
    private static string? ResolveDefaultFormat(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(Stgm.Read, out store);

            PROPERTYKEY key = PropertyKeys.PKEY_AudioEngine_DeviceFormat;
            store.GetValue(ref key, out PROPVARIANT pv);
            try
            {
                (ushort channels, ushort bits, uint sampleRate)? parsed = ParseFormatBlob(pv.GetBlobBytes());
                if (parsed == null) return null;
                (ushort channels, ushort bits, uint sampleRate) p = parsed.Value;
                return $"{p.channels} channel, {p.bits} bit, {p.sampleRate} Hz";
            }
            finally { Ole32.PropVariantClear(ref pv); }
        }
        catch
        {
            return null;
        }
        finally
        {
            Safe.Release(store);
        }
    }

    // Pulls (channels, bits, rate) out of a WAVEFORMATEX(TENSIBLE) blob. For EXTENSIBLE the
    // surfaced bit depth is wValidBitsPerSample (the actually-used bits), not wBitsPerSample
    // (the container size) - matches how mmsys.cpl labels 24-in-32 as "24 bit". Returns null
    // when the blob is too short to parse.
    private static (ushort Channels, ushort Bits, uint SampleRate)? ParseFormatBlob(byte[]? blob)
    {
        // WAVEFORMATEX is 18 bytes (16 fixed + 2 cbSize). Anything shorter can't be parsed.
        if (blob == null || blob.Length < 18) return null;

        ushort formatTag = BitConverter.ToUInt16(blob, 0);
        ushort channels = BitConverter.ToUInt16(blob, 2);
        uint sampleRate = BitConverter.ToUInt32(blob, 4);
        ushort bits = BitConverter.ToUInt16(blob, 14);

        if (formatTag == WAVE_FORMAT_EXTENSIBLE && blob.Length >= 22)
        {
            ushort valid = BitConverter.ToUInt16(blob, 18);
            if (valid > 0) bits = valid;
        }
        return (channels, bits, sampleRate);
    }

    /// <summary>
    /// Current default format split into raw numbers - the channel-count prefix and the
    /// (bits, rate) selection the format-picker context menu highlights. Null when the property
    /// is missing or the blob is too short.
    /// </summary>
    internal (int Channels, int Bits, int SampleRate)? GetCurrentFormat()
    {
        (ushort, ushort, uint)? parsed = ParseFormatBlob(ReadCurrentFormatBlob());
        if (parsed == null) return null;
        (ushort channels, ushort bits, uint rate) = parsed.Value;
        return (channels, bits, (int)rate);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drop the EAPO availability subscription before any other teardown - the handler
        // dispatches RefreshEqualizerAPOState which would otherwise race the field clears below.
        try { EqualizerAPOMonitor.AvailabilityChanged -= OnEqualizerAPOAvailabilityChanged; } catch { }

        ReleaseEndpointProxies();

        // Tear down every group's sessions, then the groups themselves. Each group disposes its
        // session subscriptions; we still own the AudioSession lifecycle (Dispose/Disconnect handlers).
        foreach (AudioAppGroup group in _groups.ToArray())
        {
            group.Empty -= OnGroupEmpty;
            foreach (AudioSession session in group.Sessions.ToArray())
            {
                session.Disconnected -= OnSessionDisconnected;
                session.StateChanged -= OnSessionStateChanged;
                Safe.Dispose(session);
            }
            Safe.Dispose(group);
        }
        _groups.Clear();
        _sessionsBySessionInstanceID.Clear();

        Safe.Release(_device);
    }

    // Endpoint volume / mute callbacks. Use the native notification only as the event-context
    // signal, then re-read the endpoint state from IAudioEndpointVolume so a stale callback payload
    // cannot poison the cached scalar with 0.
    private sealed class EndpointVolumeBridge : IAudioEndpointVolumeCallback
    {
        private readonly AudioDevice _owner;
        public EndpointVolumeBridge(AudioDevice owner) => _owner = owner;

        public int OnNotify(IntPtr pNotify)
        {
            if (pNotify == IntPtr.Zero) return 0;
            AUDIO_VOLUME_NOTIFICATION_DATA data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(pNotify);

            // Suppress echoes from our own writes.
            if (data.guidEventContext == AudioEventContext.Value) return 0;

            _owner._dispatcher.BeginInvoke(() =>
            {
                _owner.RefreshEndpointVolumeState();
            });
            return 0;
        }
    }

    private sealed class SessionNotificationBridge : IAudioSessionNotification
    {
        private readonly AudioDevice _owner;
        public SessionNotificationBridge(AudioDevice owner) => _owner = owner;

        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            // The COM ref count of newSession is given to us; AddSession takes ownership.
            _owner._dispatcher.BeginInvoke(() => _owner.AddSession(newSession));
            return 0;
        }
    }
}
