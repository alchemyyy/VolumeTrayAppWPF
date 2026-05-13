using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using VolumeTrayAppWPF.Utils;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Event-driven monitor for Bluetooth audio device battery levels.
/// <para/>
/// Windows aggregates every battery-reporting channel a Bluetooth device exposes - the BLE
/// Battery Service (GATT char 0x2A19), the HFP <c>AT+IPHONEACCEV</c> indicator, HID battery
/// usage pages - into a single PnP device property, <c>DEVPKEY_Bluetooth_Battery</c>
/// (<c>{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2</c>, byte 0-100). This is what the Settings
/// app's Bluetooth page reads; using it gets us coverage for AirPods, Sony 1000XM-series,
/// gaming headsets, mice, etc. uniformly, without protocol-specific code paths.
/// <para/>
/// Discovery: a <see cref="DeviceWatcher"/> over the Bluetooth-class PnP devnodes, with the
/// battery / container-id / connection-state keys in <c>additionalProperties</c>. The
/// <c>Updated</c> event fires whenever any of those values changes on a tracked device, so we
/// never poll - we just react.
/// <para/>
/// Attribution: an audio endpoint and the Bluetooth radio devnode that backs it share a PnP
/// container id (<see cref="PropertyKeys.PKEY_Device_ContainerId"/>), so consumers key their
/// per-device state on the container GUID rather than on a device id that differs between
/// the audio and Bluetooth views.
/// <para/>
/// Threading: WinRT delivery threads fire the watcher callbacks; we marshal the dictionary
/// mutation and the <see cref="BatteryChanged"/> fanout onto the dispatcher captured at
/// construction so consumers don't have to.
/// </summary>
internal sealed class BluetoothBatteryMonitor : INotifyPropertyChanged, IDisposable
{
    // AQS filter: PnP devnodes registered in the Bluetooth device class. The aggregated battery
    // property (DEVPKEY_Bluetooth_Battery) is set on these devnodes by the BT stack - it does not
    // surface through the modern Windows.Devices.Enumeration.DeviceInformation API at AEP or
    // AEP-Container scope (we confirmed this empirically; the key was recognized but the value
    // came back null). Falling back to the legacy PnP layer via PnpObject is what Windows
    // Settings itself does, and what Get-PnpDeviceProperty surfaces in PowerShell.
    private const string BluetoothClassGuid = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private static readonly string Selector =
        $"System.Devices.ClassGuid:=\"{BluetoothClassGuid}\"";

    // Property names recognised by the PnP property store. Battery uses the PnP "{fmtid} pid"
    // form (DEVPKEY_Bluetooth_Battery, byte 0-100); container id and connection state use their
    // system-defined canonical names.
    private const string PropertyBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PropertyContainerId = "System.Devices.ContainerId";
    private const string PropertyConnected = "System.Devices.Connected";

    private static readonly string[] RequestedProperties =
        { PropertyBattery, PropertyContainerId, PropertyConnected };

    private readonly Dispatcher _dispatcher;

    // Mutated only on the dispatcher. _idToContainer is the reverse map we use on Removed to
    // find which container a tracked device id belonged to (the Removed payload only carries the
    // id, not the property bag we saw on Added).
    private readonly Dictionary<string, Guid> _idToContainer = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _batteries = new();

    // Every container the watcher has ever reported under the BT Devices class. Independent of
    // battery state - a paired headset with no battery reporting still belongs to a BT container.
    // Grow-only: a device that's been BT stays BT for the rest of the process's lifetime, so we
    // never need to drop entries on Removed. Consumed by IsBluetoothContainer / the
    // BluetoothContainerSeen event to upgrade endpoint IsBluetooth flags that the audio property
    // store couldn't classify on its own.
    private readonly HashSet<Guid> _bluetoothContainers = new();

    // Windows assigns this GUID to any PnP devnode that doesn't belong to a real physical-device
    // container - Microsoft virtual BT stack devnodes get it, and so do many built-in audio
    // endpoints (Realtek HDA, NVIDIA HDMI, etc.) that have no meaningful container. We MUST NOT
    // treat it as a Bluetooth container: doing so promoted every non-BT audio endpoint sharing
    // the sentinel to IsBluetooth=true and propagated the A2DP codec onto them ("AAC" appearing
    // on a Realtek optical output, etc.).
    private static readonly Guid NoContainerSentinel = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    private static bool IsRealContainer(Guid g) => g != Guid.Empty && g != NoContainerSentinel;

    private PnpObjectWatcher? _watcher;
    private DispatcherTimer? _pollTimer;
    private bool _isRunning;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fires on the dispatcher whenever a container's battery percentage transitions (including
    /// to null on remove / disconnect). Subscribers can update bindings directly without a
    /// Dispatcher hop of their own.
    /// </summary>
    public event Action<Guid, int?>? BatteryChanged;

    /// <summary>
    /// Fires on the dispatcher the first time a given BT container surfaces through the watcher.
    /// Audio code uses this as the definitive "this container is Bluetooth" signal - the audio
    /// endpoint property store's enumerator key isn't reliably populated on all Win11 drivers,
    /// so endpoints that share a container id with a known BT devnode get promoted to IsBluetooth
    /// after the watcher reports the container.
    /// </summary>
    public event Action<Guid>? BluetoothContainerSeen;

    public BluetoothBatteryMonitor(Dispatcher dispatcher) { _dispatcher = dispatcher; }

    /// <summary>True once the watcher is started and pumping events.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Last known battery percentage (0-100) for the given container id, or null when unknown.
    /// Used by the manager to seed a newly-wrapped Bluetooth endpoint with the cached value so
    /// it doesn't paint blank until the next OS update.
    /// </summary>
    public int? TryGet(Guid containerId)
    {
        return _batteries.TryGetValue(containerId, out int v) ? v : (int?)null;
    }

    /// <summary>
    /// True if <paramref name="containerId"/> has been observed under the BT Devices class AQS
    /// filter at any point since <see cref="Start"/>. Independent of battery state - a paired
    /// device with no battery reporting still trips this. Safe to call from any thread; the
    /// set is mutated only on the dispatcher and HashSet reads are race-tolerant for the
    /// "have we ever seen this guid" question (false negatives during enumeration are fine
    /// since BluetoothContainerSeen fires on the same dispatcher pass).
    /// </summary>
    public bool IsBluetoothContainer(Guid containerId) => _bluetoothContainers.Contains(containerId);

    /// <summary>
    /// Creates the DeviceWatcher and starts pumping events. Idempotent; failures (rare - AQS
    /// rejection or COM apartment misconfiguration) are logged and leave the monitor inert with
    /// <see cref="IsRunning"/> = false rather than throwing into the caller.
    /// </summary>
    public void Start()
    {
        if (_disposed || _isRunning) return;
        try
        {
            _watcher = PnpObject.CreateWatcher(PnpObjectType.Device, RequestedProperties, Selector);
            _watcher.Added += OnDeviceAdded;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Start();
            IsRunning = true;
            WPFLog.Log("BluetoothBatteryMonitor.Start: PnpObject watcher started.");
        }
        catch (Exception ex)
        {
            WPFLog.Log($"BluetoothBatteryMonitor.Start: failed: {ex.Message}");
            DetachWatcher();
        }
    }

    /// <summary>
    /// Begins periodic active polls of every tracked BT devnode's battery via
    /// <c>CM_Get_DevNode_Property</c>. The watcher doesn't push battery deltas (Windows only sends
    /// Updated events for Connected-state changes), so an explicit poll is what surfaces a
    /// changing percentage to the UI. Scoped to flyout visibility by the caller - no point hitting
    /// the OS when nothing is bound. Idempotent; calls while already polling are no-ops.
    /// </summary>
    public void StartPolling()
    {
        if (_disposed || _pollTimer != null) return;
        WPFLog.Log($"BluetoothBatteryMonitor.StartPolling: tracking {_idToContainer.Count} devnodes");
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(TimeConstants.BluetoothBatteryPollIntervalMs),
        };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
        // Fire one synchronous poll immediately so the flyout opens with a fresh value rather
        // than the last-known cached one (or none, on first open).
        OnPollTick(this, EventArgs.Empty);
    }

    /// <summary>
    /// Stops and discards the poll timer. The watcher keeps running so container-seen events
    /// and the cached battery values stay current for the next StartPolling. Idempotent.
    /// </summary>
    public void StopPolling()
    {
        if (_pollTimer == null) return;
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        _pollTimer = null;
    }

    // Dispatcher-thread tick. Scans every present PnP devnode (the property is set on a devnode
    // we can't predict by class - HFP service, HID, audio endpoint, depends on device), reads
    // DEVPKEY_Bluetooth_Battery, and on any hit also reads DEVPKEY_Device_ContainerId so the
    // value can be attributed back to the audio endpoint sharing that container. Logs a single
    // scanned / matched summary per tick.
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        List<string> ids = EnumeratePresentDevnodeIds();
        int matched = 0;

        for (int i = 0; i < ids.Count; i++)
        {
            string deviceId = ids[i];
            int cr = CfgMgr32.CM_Locate_DevNodeW(out uint devInst, deviceId, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr32.CR_SUCCESS) continue;

            int? battery = TryReadByteProperty(devInst, CfgMgr32.DEVPKEY_Bluetooth_Battery);
            if (!battery.HasValue) continue;

            Guid? container = TryReadGuidProperty(devInst, CfgMgr32.DEVPKEY_Device_ContainerId);
            if (!container.HasValue || !IsRealContainer(container.Value)) continue;

            matched++;
            WPFLog.Log($"BluetoothBatteryMonitor.Poll: hit id='{deviceId}' container={container.Value} battery={battery.Value}");

            // Side-effect: mark the container as BT (the watcher's class-filtered view may miss
            // it) and fire BluetoothContainerSeen so AudioDevice.IsBluetooth flips for matching
            // endpoints. Same path Apply takes for watcher-discovered containers.
            _idToContainer[deviceId] = container.Value;
            if (_bluetoothContainers.Add(container.Value))
            {
                try { BluetoothContainerSeen?.Invoke(container.Value); }
                catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor: container-seen subscriber threw: {ex.Message}"); }
            }
            ApplyBattery(container.Value, battery);
        }

        WPFLog.Log($"BluetoothBatteryMonitor.Poll: scanned={ids.Count} matched={matched}");
    }

    private void OnDeviceAdded(PnpObjectWatcher sender, PnpObject info)
    {
        string id = info.Id;
        // Watcher payload's battery / connected keys are always null on this surface (confirmed
        // empirically) - only the container id is useful here. The actual battery read happens
        // out of OnPollTick via a system-wide CM_Get_Device_ID_List scan.
        Guid? container = ReadGuidProperty(info.Properties, PropertyContainerId);
        try { _dispatcher.BeginInvoke(() => Apply(id, container, null, null)); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor.OnDeviceAdded dispatch: {ex.Message}"); }
    }

    private void OnDeviceUpdated(PnpObjectWatcher sender, PnpObjectUpdate update)
    {
        string id = update.Id;
        Guid? container = ReadGuidProperty(update.Properties, PropertyContainerId);
        try { _dispatcher.BeginInvoke(() => Apply(id, container, null, null)); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor.OnDeviceUpdated dispatch: {ex.Message}"); }
    }

    // CM_Get_DevNode_Property: read a single byte property (DEVPROP_TYPE_BYTE) off a located
    // devnode handle. Returns null on any CR_* failure / type mismatch / out-of-range value.
    private static int? TryReadByteProperty(uint devInst, CfgMgr32.DEVPROPKEY key)
    {
        uint size = 0;
        int cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, ref key, out uint propType, null, ref size, 0);
        if (cr != CfgMgr32.CR_BUFFER_SMALL && cr != CfgMgr32.CR_SUCCESS) return null;
        if (propType != CfgMgr32.DEVPROP_TYPE_BYTE || size < 1) return null;

        byte[] buf = new byte[size];
        cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, ref key, out propType, buf, ref size, 0);
        if (cr != CfgMgr32.CR_SUCCESS) return null;

        int level = buf[0];
        return (level >= 0 && level <= 100) ? level : null;
    }

    // CM_Get_DevNode_Property: read a 16-byte GUID property (DEVPROP_TYPE_GUID). Used to get
    // DEVPKEY_Device_ContainerId off the same devnode that carries the battery, for attribution
    // back to the audio endpoint's wrapper.
    private static Guid? TryReadGuidProperty(uint devInst, CfgMgr32.DEVPROPKEY key)
    {
        uint size = 0;
        int cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, ref key, out uint propType, null, ref size, 0);
        if (cr != CfgMgr32.CR_BUFFER_SMALL && cr != CfgMgr32.CR_SUCCESS) return null;
        if (propType != CfgMgr32.DEVPROP_TYPE_GUID || size != 16) return null;

        byte[] buf = new byte[16];
        cr = CfgMgr32.CM_Get_DevNode_PropertyW(devInst, ref key, out propType, buf, ref size, 0);
        if (cr != CfgMgr32.CR_SUCCESS) return null;

        return new Guid(buf);
    }

    // CM_Get_Device_ID_List(null, PRESENT): every PnP devnode currently present on the system,
    // as a double-null-terminated multi-string. The Bluetooth battery property is set on a
    // devnode whose class is *not* the Bluetooth Devices class (the PnpObject watcher's filter
    // hides it), so we scan the whole list and let the property-presence check pick the right
    // one. The PowerShell reference (Get-PnpDevice -FriendlyName '*WH-1000XM4*') and the Rust
    // reference both take the same "scan everything, no class filter" approach.
    private static List<string> EnumeratePresentDevnodeIds()
    {
        List<string> ids = new(capacity: 512);

        int cr = CfgMgr32.CM_Get_Device_ID_List_SizeW(out uint size, null, CfgMgr32.CM_GETIDLIST_FILTER_PRESENT);
        if (cr != CfgMgr32.CR_SUCCESS || size == 0) return ids;

        char[] buffer = new char[size];
        cr = CfgMgr32.CM_Get_Device_ID_ListW(null, buffer, size, CfgMgr32.CM_GETIDLIST_FILTER_PRESENT);
        if (cr != CfgMgr32.CR_SUCCESS) return ids;

        // Multi-string: null-terminated entries, double-null at end. An empty string (immediate
        // null) marks the terminator - bail out so we don't append zero-length entries.
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0') continue;
            if (i == start) break;
            ids.Add(new string(buffer, start, i - start));
            start = i + 1;
        }
        return ids;
    }

    // Diagnostic: one-line summary of the watcher's property bag for a single event. Lists each
    // key with the projected CLR type and a short value preview so we can see whether the battery
    // key is arriving and how it's typed. Best-effort - any exception falls back to "<error>".
    private static string FormatProps(IReadOnlyDictionary<string, object> props)
    {
        try
        {
            if (props.Count == 0) return "{}";
            System.Text.StringBuilder sb = new();
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in props)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key).Append('=');
                if (kv.Value == null) sb.Append("<null>");
                else sb.Append('(').Append(kv.Value.GetType().Name).Append(')').Append(kv.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }
        catch { return "<error>"; }
    }

    private void OnDeviceRemoved(PnpObjectWatcher sender, PnpObjectUpdate update)
    {
        string id = update.Id;
        try { _dispatcher.BeginInvoke(() => ApplyRemoved(id)); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor.OnDeviceRemoved dispatch: {ex.Message}"); }
    }

    // Dispatcher-only. Merges an Added / Updated event into the cache and emits BatteryChanged
    // when the effective battery value for the container actually transitions.
    private void Apply(string id, Guid? container, int? battery, bool? connected)
    {
        if (_disposed) return;

        // ContainerId can be absent from an Updated payload when only battery / connection state
        // changed; fall back to the cached mapping. If we've never seen this id and no container
        // came through, drop the event - nothing to attribute the change to.
        Guid containerId;
        if (container is Guid c)
        {
            containerId = c;
            _idToContainer[id] = c;
        }
        else if (!_idToContainer.TryGetValue(id, out containerId))
        {
            return;
        }

        // Record the container in the BT set the first time we see it, regardless of whether
        // battery / connection state changed in this event. Audio code relies on this to upgrade
        // IsBluetooth on endpoints whose property store didn't surface BTHENUM. Sentinel /
        // empty containers are PnP "no real container" placeholders and would falsely match
        // any other endpoint that also has no container (built-in Realtek HDA etc).
        if (IsRealContainer(containerId) && _bluetoothContainers.Add(containerId))
        {
            WPFLog.Log($"BluetoothBatteryMonitor: new BT container={containerId}");
            try { BluetoothContainerSeen?.Invoke(containerId); }
            catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor: container-seen subscriber threw: {ex.Message}"); }
        }

        // A disconnected device's last battery reading is stale; treat as unknown. We don't
        // unilaterally clear the cache on a connect=true with no battery field though - that's
        // a "no change to battery" notification, not a "battery just became unknown" one.
        int? effective;
        if (connected == false) effective = null;
        else if (battery.HasValue) effective = battery;
        else return; // neither connection nor battery field updated meaningfully

        // Sentinel containers would fan a battery value to every unrelated endpoint that also
        // lacks a real container; skip the cache write so OnBluetoothBatteryChanged never fires
        // against the sentinel.
        if (!IsRealContainer(containerId)) return;
        ApplyBattery(containerId, effective);
    }

    // Dispatcher-only. Drop the id->container mapping for a removed devnode and, if no other
    // tracked id still maps to that container, clear and emit the battery transition.
    private void ApplyRemoved(string id)
    {
        if (_disposed) return;
        if (!_idToContainer.TryGetValue(id, out Guid containerId)) return;
        _idToContainer.Remove(id);

        // Same container can be exposed through multiple devnodes (HID battery devnode + audio
        // devnode for a single headset, etc.). Only collapse the cached battery when the last
        // tracked id for that container disappears.
        foreach (KeyValuePair<string, Guid> kv in _idToContainer)
        {
            if (kv.Value == containerId) return;
        }
        ApplyBattery(containerId, null);
    }

    private void ApplyBattery(Guid containerId, int? newValue)
    {
        bool changed;
        if (newValue is int v)
        {
            changed = !_batteries.TryGetValue(containerId, out int existing) || existing != v;
            _batteries[containerId] = v;
        }
        else
        {
            changed = _batteries.Remove(containerId);
        }
        if (!changed) return;

        WPFLog.Log($"BluetoothBatteryMonitor: container={containerId} battery={(newValue?.ToString() ?? "<null>")}");

        try { BatteryChanged?.Invoke(containerId, newValue); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor: subscriber threw: {ex.Message}"); }
    }

    // Property-bag readers. WinRT projects DEVPROP types as boxed CLR primitives - GUID as
    // System.Guid, byte (battery) as System.Byte, bool as System.Boolean - but a couple of
    // older Windows builds box the container id as a string. Handle both shapes defensively.
    private static Guid? ReadGuidProperty(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out object? value) || value == null) return null;
        if (value is Guid g) return g;
        if (value is string s && Guid.TryParse(s, out Guid parsed)) return parsed;
        return null;
    }

    private static int? ReadByteProperty(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out object? value) || value == null) return null;
        try
        {
            int n = Convert.ToInt32(value);
            // Clamp into [0, 100] - some drivers report 0xFF as "unknown".
            if (n < 0 || n > 100) return null;
            return n;
        }
        catch { return null; }
    }

    private static bool? ReadBoolProperty(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out object? value) || value == null) return null;
        return value is bool b ? b : (bool?)null;
    }

    private void DetachWatcher()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.Added -= OnDeviceAdded;
            _watcher.Updated -= OnDeviceUpdated;
            _watcher.Removed -= OnDeviceRemoved;
            // Stop is only valid on Started / EnumerationCompleted; calling it on Created /
            // Stopping / Stopped raises. Mirrors the EarTrumpet DeviceWatcher teardown shape.
            DeviceWatcherStatus s = _watcher.Status;
            if (s == DeviceWatcherStatus.Started || s == DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }
        }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor: detach: {ex.Message}"); }
        _watcher = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;
        _pollTimer?.Stop();
        _pollTimer = null;
        DetachWatcher();
        _idToContainer.Clear();
        _batteries.Clear();
        _bluetoothContainers.Clear();
    }
}

/// <summary>
/// Minimal cfgmgr32 P/Invoke surface for reading DEVPKEY_Bluetooth_Battery directly off a PnP
/// instance id. The modern WinRT enumeration APIs (DeviceInformation, PnpObject) project the
/// property key but always return null for its value; the Configuration Manager layer is where
/// the value actually lives, and it's what Get-PnpDeviceProperty + Settings read through.
/// </summary>
internal static class CfgMgr32
{
    // CR_* return codes used by the readers above. The full set is much larger; we only need
    // SUCCESS (the value was read), BUFFER_SMALL (size-probe call - expected on the first
    // CM_Get_DevNode_Property), and the two "no value" codes the OS returns when a devnode
    // exists but doesn't carry the property.
    public const int CR_SUCCESS = 0x00000000;
    public const int CR_BUFFER_SMALL = 0x0000001A;
    public const int CR_NO_SUCH_VALUE = 0x00000025;

    public const int CM_LOCATE_DEVNODE_NORMAL = 0;
    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    public const uint DEVPROP_TYPE_BYTE = 0x00000003;
    public const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Bluetooth_Battery: {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid 2. Byte 0-100.
    public static readonly DEVPROPKEY DEVPKEY_Bluetooth_Battery = new()
    {
        fmtid = new Guid(0x104EA319, 0x6EE2, 0x4701, 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5),
        pid = 2,
    };

    // DEVPKEY_Device_ContainerId: {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C} pid 2. 16-byte GUID.
    // Set on every interface a single physical device exposes, so an audio endpoint's container
    // id matches the BT-protocol devnode (BTHENUM, BTHHFENUM, etc.) that carries the battery.
    public static readonly DEVPROPKEY DEVPKEY_Device_ContainerId = new()
    {
        fmtid = new Guid(0x8C7ED206, 0x3F8A, 0x4827, 0xB3, 0xAB, 0xAE, 0x9E, 0x1F, 0xAE, 0xFC, 0x6C),
        pid = 2,
    };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst,
        [In] string pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        [Out] byte[]? PropertyBuffer,
        ref uint PropertyBufferSize,
        uint ulFlags);

    // System-wide devnode enumeration. pszFilter=null retrieves every present PnP devnode (with
    // CM_GETIDLIST_FILTER_PRESENT) as a double-null-terminated multi-string. Size is in chars,
    // not bytes; the size-probe call returns the count of UTF-16 chars including the trailing
    // pair of nulls.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_Device_ID_List_SizeW(
        out uint pulLen,
        [In] string? pszFilter,
        uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CM_Get_Device_ID_ListW(
        [In] string? pszFilter,
        [Out] char[] buffer,
        uint bufferLen,
        uint ulFlags);
}
