using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using VolumeTrayAppWPF.Utils;
using Windows.Devices.Enumeration;

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
    // AQS filter: PnP devnodes on the Bluetooth Devices class. The Bluetooth radio devnode
    // (parent of audio / HID / etc. interfaces) is where Windows surfaces the aggregated battery
    // property; it shares its container id with every interface the physical headset exposes.
    private const string BluetoothClassGuid = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private static readonly string Selector =
        $"System.Devices.ClassGuid:=\"{BluetoothClassGuid}\"";

    // Canonical names recognized by the WinRT DeviceInformation projection. The battery key
    // uses PnP "{fmtid} pid" form; container id and connection state use their system-defined
    // PROPERTYDESCRIPTION names.
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

    private DeviceWatcher? _watcher;
    private bool _isRunning;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fires on the dispatcher whenever a container's battery percentage transitions (including
    /// to null on remove / disconnect). Subscribers can update bindings directly without a
    /// Dispatcher hop of their own.
    /// </summary>
    public event Action<Guid, int?>? BatteryChanged;

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
    /// Creates the DeviceWatcher and starts pumping events. Idempotent; failures (rare - AQS
    /// rejection or COM apartment misconfiguration) are logged and leave the monitor inert with
    /// <see cref="IsRunning"/> = false rather than throwing into the caller.
    /// </summary>
    public void Start()
    {
        if (_disposed || _isRunning) return;
        try
        {
            _watcher = DeviceInformation.CreateWatcher(Selector, RequestedProperties,
                DeviceInformationKind.Device);
            _watcher.Added += OnDeviceAdded;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.Start();
            IsRunning = true;
            WPFLog.Log("BluetoothBatteryMonitor.Start: watcher started.");
        }
        catch (Exception ex)
        {
            WPFLog.Log($"BluetoothBatteryMonitor.Start: failed: {ex.Message}");
            DetachWatcher();
        }
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        string id = info.Id;
        Guid? container = ReadGuidProperty(info.Properties, PropertyContainerId);
        int? battery = ReadByteProperty(info.Properties, PropertyBattery);
        bool? connected = ReadBoolProperty(info.Properties, PropertyConnected);
        try { _dispatcher.BeginInvoke(() => Apply(id, container, battery, connected)); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor.OnDeviceAdded dispatch: {ex.Message}"); }
    }

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        string id = update.Id;
        // Update.Properties contains only the keys whose value changed since the last event.
        // Any of the three may be absent on a given update; absent container id falls back to
        // the cached mapping in Apply.
        Guid? container = ReadGuidProperty(update.Properties, PropertyContainerId);
        int? battery = ReadByteProperty(update.Properties, PropertyBattery);
        bool? connected = ReadBoolProperty(update.Properties, PropertyConnected);
        try { _dispatcher.BeginInvoke(() => Apply(id, container, battery, connected)); }
        catch (Exception ex) { WPFLog.Log($"BluetoothBatteryMonitor.OnDeviceUpdated dispatch: {ex.Message}"); }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
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

        // A disconnected device's last battery reading is stale; treat as unknown. We don't
        // unilaterally clear the cache on a connect=true with no battery field though - that's
        // a "no change to battery" notification, not a "battery just became unknown" one.
        int? effective;
        if (connected == false) effective = null;
        else if (battery.HasValue) effective = battery;
        else return; // neither connection nor battery field updated meaningfully

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
        DetachWatcher();
        _idToContainer.Clear();
        _batteries.Clear();
    }
}
