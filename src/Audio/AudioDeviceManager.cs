using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Timers;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Top-level audio service. Owns the IMMDeviceEnumerator, tracks the live default render endpoint,
/// and exposes the live device list. Implements <see cref="IMMNotificationClient"/> so device add /
/// remove / default-change notifications keep the wrapper state in sync without polling.
/// All observable state mutations are marshaled onto the UI dispatcher captured at construction.
/// Peak metering for all visible sessions is driven by a single dispatcher timer that the host can
/// start / stop based on flyout visibility.
/// </summary>
internal sealed class AudioDeviceManager : INotifyPropertyChanged, IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationBridge _bridge;
    private readonly Dispatcher _dispatcher;
    // Threadpool-fired timers. Sample timer's Elapsed runs on a bg thread and reads COM peaks
    // off the UI thread, then BeginInvokes the lerp arming back to the dispatcher. Render timer's
    // Elapsed BeginInvokes the lerp advancement onto the dispatcher. Mirrors EarTrumpet's split-
    // thread pattern - keeps the UI dispatcher free even at high sample rates.
    private readonly System.Timers.Timer _peakSampleTimer;
    private readonly System.Timers.Timer _peakRenderTimer;
    private readonly ObservableCollection<AudioDevice> _devices = [];
    private readonly AppSettings? _settings;

    // Shared rate-limiter for SetMasterVolume(Level)Scalar writes. Sliders update _volume + raise
    // PropertyChanged synchronously so the UI stays responsive, but the underlying COM write is
    // queued through this throttler with latest-pending-wins semantics. One throttler is enough -
    // it keys per device/session id internally so independent sliders don't block each other.
    private readonly AsyncThrottler<string> _volumeThrottler;

    // Watches every audio session's PID; on exit fires a callback the session uses to mark itself
    // disconnected. Closes the gap where the audio engine leaves a session as Inactive long after
    // the owning process is force-killed instead of firing Expired promptly.
    private readonly ProcessExitMonitor _processExitMonitor;

    private AudioDevice? _defaultDevice;
    private bool _disposed;

    public ReadOnlyObservableCollection<AudioDevice> Devices { get; }

    /// <summary>
    /// The current default render endpoint. May be null briefly during device transitions
    /// (e.g. between an unplug and the OS picking a successor).
    /// </summary>
    public AudioDevice? DefaultDevice
    {
        get => _defaultDevice;
        private set { if (!ReferenceEquals(_defaultDevice, value)) { _defaultDevice = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AudioDeviceManager(Dispatcher dispatcher, AppSettings? settings = null)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        Devices = new ReadOnlyObservableCollection<AudioDevice>(_devices);

        _volumeThrottler = new AsyncThrottler<string>(TimeConstants.VolumeWriteRateDefaultMs, StringComparer.Ordinal);
        _processExitMonitor = new ProcessExitMonitor();

        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        // Sample timer's Elapsed fires on the threadpool and does the COM peak read off the UI
        // thread; render timer's Elapsed marshals the lerp advancement onto the dispatcher.
        // SynchronizingObject left null on purpose so Elapsed runs on the threadpool rather than
        // any captured sync context.
        _peakSampleTimer = new System.Timers.Timer(ResolveSampleIntervalMs())
        {
            AutoReset = true,
        };
        _peakSampleTimer.Elapsed += OnPeakSampleElapsed;

        _peakRenderTimer = new System.Timers.Timer(ResolveRenderIntervalMs())
        {
            AutoReset = true,
        };
        _peakRenderTimer.Elapsed += OnPeakRenderElapsed;

        // Retune timers immediately when the user changes the rates from the settings page.
        // System.Timers.Timer.Interval is thread-safe and reschedules on the next tick.
        if (_settings != null)
        {
            _settings.MeterPeakFpsChanged += OnMeterPeakFpsChanged;
            _settings.MeterPeakSampleRateChanged += OnMeterPeakSampleRateChanged;
        }

        _bridge = new NotificationBridge(this);
        _enumerator.RegisterEndpointNotificationCallback(_bridge);

        RebuildDeviceList();
    }

    private double ResolveRenderIntervalMs()
    {
        int fps = _settings?.MeterPeakFps ?? AppSettings.MeterPeakFpsDefault;
        if (fps < 1) fps = 1;
        return 1000.0 / fps;
    }

    private double ResolveSampleIntervalMs()
    {
        int rate = _settings?.MeterPeakSampleRate ?? AppSettings.MeterPeakSampleRateDefault;
        if (rate < 1) rate = 1;
        return 1000.0 / rate;
    }

    /// <summary>
    /// How many render frames span one sample interval. Drives the lerp denominator: at FPS=180
    /// SampleRate=90 the result is 2, so each sample is rendered over 2 frames - first the midpoint,
    /// then snap-to-target. Clamped to at least 1 so FPS &lt; SampleRate degrades to "snap on every
    /// render" rather than dividing by zero.
    /// </summary>
    private int ResolveInterpolationSteps()
    {
        int fps = _settings?.MeterPeakFps ?? AppSettings.MeterPeakFpsDefault;
        int rate = _settings?.MeterPeakSampleRate ?? AppSettings.MeterPeakSampleRateDefault;
        if (rate < 1) rate = 1;
        return Math.Max(1, fps / rate);
    }

    private void OnMeterPeakFpsChanged() => _peakRenderTimer.Interval = ResolveRenderIntervalMs();

    private void OnMeterPeakSampleRateChanged() => _peakSampleTimer.Interval = ResolveSampleIntervalMs();

    /// <summary>Starts the peak-meter polling + render timers. Called when the flyout becomes visible.</summary>
    public void StartMetering()
    {
        _peakSampleTimer.Start();
        _peakRenderTimer.Start();
    }

    /// <summary>Stops both timers. Called when the flyout hides so the app stays idle.</summary>
    public void StopMetering()
    {
        _peakSampleTimer.Stop();
        _peakRenderTimer.Stop();
    }

    /// <summary>
    /// Bg-thread sample tick. Snapshots the device list under try/catch (UI mutations can tear
    /// ToArray's enumerator), reads each device's COM peak off the UI thread, then dispatches
    /// the UI-thread interpolation arming through <see cref="OnNewSample"/>. Mirrors EarTrumpet:
    /// the dispatcher only sees the lerp work, never the COM call.
    /// </summary>
    private void OnPeakSampleElapsed(object? sender, ElapsedEventArgs e)
    {
        int steps = ResolveInterpolationSteps();

        AudioDevice[] devices;
        try { devices = _devices.ToArray(); }
        catch
        {
            // Concurrent mutation of _devices on the UI thread tore the enumerator. Skip this
            // tick - the next 33 ms tick will pick up the updated list.
            return;
        }

        for (int i = 0; i < devices.Length; i++)
        {
            try { devices[i].UpdatePeakValueBackground(); }
            catch { /* device may have died between callbacks */ }
        }

        _dispatcher.BeginInvoke(() =>
        {
            for (int i = _devices.Count - 1; i >= 0; i--)
            {
                try { _devices[i].OnNewSample(steps); }
                catch { /* device may have died between callbacks */ }
            }
        });
    }

    /// <summary>
    /// Bg-thread render tick. Marshals the per-frame lerp advancement onto the dispatcher; the
    /// device.OnRenderTick walk and the PropertyChanged that drives the bound MeterPeak overlay
    /// both run on the UI thread.
    /// </summary>
    private void OnPeakRenderElapsed(object? sender, ElapsedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            for (int i = _devices.Count - 1; i >= 0; i--)
            {
                try { _devices[i].OnRenderTick(); }
                catch { /* device may have died between callbacks */ }
            }
        });
    }

    /// <summary>
    /// One-shot full enumeration. Used at startup; runtime device events take the incremental
    /// add / remove paths so unrelated devices don't lose their session state on every plug event.
    /// </summary>
    private void RebuildDeviceList()
    {
        foreach (AudioDevice d in _devices.ToArray()) d.Dispose();
        _devices.Clear();

        IMMDeviceCollection? collection = null;
        try
        {
            _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out collection);
            collection.GetCount(out uint count);

            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out IMMDevice device);
                AudioDevice? wrapped = WrapOrRelease(device);
                if (wrapped != null) _devices.Add(wrapped);
            }
        }
        catch
        {
            // Enumeration can fail mid-suspend / device transition; keep what we have.
        }
        finally
        {
            if (collection != null) Marshal.FinalReleaseComObject(collection);
        }

        UpdateDefaultDevice();
    }

    /// <summary>
    /// Add a device by ID if it's an active render endpoint we don't already track.
    /// No-op when the device is wrong-flow (capture), wrong-state (disabled, unplugged),
    /// or already present. Used for OnDeviceAdded / OnDeviceStateChanged(active) paths.
    /// </summary>
    private void AddDeviceById(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (FindDeviceById(id) != null) return;

        IMMDevice? device = null;
        try
        {
            _enumerator.GetDevice(id, out device);
            if (device == null) return;

            // Only render endpoints. The same OnDeviceAdded fires for capture too; ignore those.
            if (!IsRenderEndpoint(device)) { Marshal.FinalReleaseComObject(device); return; }

            // Active state only. Disabled / NotPresent / Unplugged devices shouldn't appear in the list.
            device.GetState(out uint state);
            if ((state & (uint)DeviceState.Active) == 0) { Marshal.FinalReleaseComObject(device); return; }

            AudioDevice? wrapped = WrapOrRelease(device);
            if (wrapped != null)
            {
                _devices.Add(wrapped);
                UpdateDefaultDevice();
            }
        }
        catch
        {
            // Device gone between notification and our query; nothing to add.
            if (device != null) { try { Marshal.FinalReleaseComObject(device); } catch { } }
        }
    }

    /// <summary>
    /// Remove a single device wrapper by ID, disposing its sessions and releasing its COM proxies.
    /// Used for OnDeviceRemoved and OnDeviceStateChanged(non-active) paths.
    /// </summary>
    private void RemoveDeviceById(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        AudioDevice? match = FindDeviceById(id);
        if (match == null) return;

        _devices.Remove(match);
        try { match.Dispose(); } catch { }
        UpdateDefaultDevice();
    }

    /// <summary>
    /// OnDeviceStateChanged demultiplexer: a device transitioning to Active is an effective add,
    /// any other state is an effective remove. Render-flow filtering happens inside the add path.
    /// </summary>
    private void HandleDeviceStateChanged(string id, uint newState)
    {
        if (string.IsNullOrEmpty(id)) return;
        if ((newState & (uint)DeviceState.Active) != 0) AddDeviceById(id);
        else RemoveDeviceById(id);
    }

    /// <summary>
    /// Update one device's friendly name in place when the OS reports PKEY_Device_FriendlyName changed.
    /// Avoids the full RebuildDeviceList that would otherwise drop every other device's session list.
    /// </summary>
    private void RefreshDeviceFriendlyName(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        AudioDevice? match = FindDeviceById(id);
        if (match == null) return;
        match.RefreshFriendlyNameFromStore();
    }

    private AudioDevice? FindDeviceById(string id)
    {
        foreach (AudioDevice d in _devices)
        {
            if (d.Id == id) return d;
        }
        return null;
    }

    private static bool IsRenderEndpoint(IMMDevice device)
    {
        IMMEndpoint? endpoint = null;
        try
        {
            endpoint = device as IMMEndpoint;
            if (endpoint == null) return false;
            endpoint.GetDataFlow(out EDataFlow flow);
            return flow == EDataFlow.eRender;
        }
        catch { return false; }
    }

    private AudioDevice? WrapOrRelease(IMMDevice device)
    {
        try { return new AudioDevice(device, _dispatcher, _volumeThrottler, _processExitMonitor); }
        catch
        {
            try { Marshal.FinalReleaseComObject(device); } catch { }
            return null;
        }
    }

    private void UpdateDefaultDevice()
    {
        IMMDevice? defaultDevice = null;
        string? defaultId = null;
        try
        {
            // GetDefaultAudioEndpoint returns E_NOTFOUND (0x80070490) when no render endpoint exists
            // (common right after the last endpoint is unplugged); treat that as "no default".
            int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out defaultDevice);
            if (hr < 0 || defaultDevice == null)
            {
                MarkDefault(null);
                return;
            }

            defaultDevice.GetId(out defaultId);
        }
        catch
        {
            MarkDefault(null);
            return;
        }
        finally
        {
            if (defaultDevice != null) Marshal.FinalReleaseComObject(defaultDevice);
        }

        // Match by ID against our wrapped list.
        AudioDevice? match = null;
        foreach (AudioDevice d in _devices)
        {
            if (d.Id == defaultId) { match = d; break; }
        }
        MarkDefault(match);
    }

    private void MarkDefault(AudioDevice? newDefault)
    {
        foreach (AudioDevice d in _devices) d.IsDefault = ReferenceEquals(d, newDefault);
        DefaultDevice = newDefault;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _peakSampleTimer.Stop();
        _peakRenderTimer.Stop();
        _peakSampleTimer.Elapsed -= OnPeakSampleElapsed;
        _peakRenderTimer.Elapsed -= OnPeakRenderElapsed;
        try { _peakSampleTimer.Dispose(); } catch { }
        try { _peakRenderTimer.Dispose(); } catch { }
        if (_settings != null)
        {
            _settings.MeterPeakFpsChanged -= OnMeterPeakFpsChanged;
            _settings.MeterPeakSampleRateChanged -= OnMeterPeakSampleRateChanged;
        }

        try { _enumerator.UnregisterEndpointNotificationCallback(_bridge); } catch { }

        foreach (AudioDevice d in _devices.ToArray())
        {
            try { d.Dispose(); } catch { }
        }
        _devices.Clear();

        // Dispose the throttler last - any payload still in flight will see _disposed on the
        // RCW it captured and bail out via its inner try/catch. Letting it run to completion
        // is preferable to forcibly cancelling, which can race with finalization.
        try { _volumeThrottler.Dispose(); } catch { }

        // Tear the watcher thread down after every device (and so every session) is disposed -
        // sessions Unwatch on Dispose, so by the time we get here the watch set is empty and the
        // monitor's worker thread is just blocked on the wake event.
        try { _processExitMonitor.Dispose(); } catch { }

        try { Marshal.FinalReleaseComObject(_enumerator); } catch { }
    }

    // Notification callbacks fire on COM worker threads; everything is marshaled to the dispatcher
    // before mutating observable state.
    private sealed class NotificationBridge : IMMNotificationClient
    {
        private readonly AudioDeviceManager _owner;
        public NotificationBridge(AudioDeviceManager owner) { _owner = owner; }

        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState)
        {
            // Active <-> Disabled / Unplugged transitions add or remove a single device. Incremental
            // path preserves session state on every other device, which a full rebuild would discard.
            string id = pwstrDeviceId;
            uint state = dwNewState;
            _owner._dispatcher.BeginInvoke(() => _owner.HandleDeviceStateChanged(id, state));
            return 0;
        }

        public int OnDeviceAdded(string pwstrDeviceId)
        {
            string id = pwstrDeviceId;
            _owner._dispatcher.BeginInvoke(() => _owner.AddDeviceById(id));
            return 0;
        }

        public int OnDeviceRemoved(string pwstrDeviceId)
        {
            string id = pwstrDeviceId;
            _owner._dispatcher.BeginInvoke(() => _owner.RemoveDeviceById(id));
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? pwstrDefaultDeviceId)
        {
            // Only the render+multimedia default drives our flyout selection; ignore the rest.
            if (flow != EDataFlow.eRender || role != ERole.eMultimedia) return 0;
            _owner._dispatcher.BeginInvoke(() => _owner.UpdateDefaultDevice());
            return 0;
        }

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key)
        {
            // Friendly-name renames from the OS Settings page arrive here. Refresh that one device's
            // name in place rather than rebuilding the world.
            if (key.fmtid == PropertyKeys.PKEY_Device_FriendlyName.fmtid &&
                key.pid == PropertyKeys.PKEY_Device_FriendlyName.pid)
            {
                string id = pwstrDeviceId;
                _owner._dispatcher.BeginInvoke(() => _owner.RefreshDeviceFriendlyName(id));
            }
            return 0;
        }
    }
}
