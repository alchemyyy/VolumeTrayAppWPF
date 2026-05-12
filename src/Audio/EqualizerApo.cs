using System.IO;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Per-device Equalizer APO state projected onto the device-row equalizer button.
/// </summary>
///   NotAvailable      Equalizer APO is not detected on this system at all - the button glyph dims
///                     and overlays SIGNAL_NOT_CONNECTED. Clicking shows the install / locate dialog.
///   NotInstalled      APO binary exists but no APO is registered against this endpoint. Button
///                     dimmed; click installs the APO chain for this device.
///   EnhancementsOff   APO is installed against this device but PKEY_AudioEndpoint_Disable_SysFx
///                     is set, so the engine bypasses every APO. Button dimmed; click re-enables.
///   Running           APO is active on this device. Button full bright; click uninstalls.
internal enum EqualizerApoState
{
    NotAvailable,
    NotInstalled,
    EnhancementsOff,
    Running,
}

/// <summary>
/// Live availability monitor for Equalizer APO. Watches the well-known install dir and the
/// uninstall-registry hive so user-driven install / removal flips the per-device button state
/// without a flyout reopen. Backend probing is stubbed for now - the monitor wires the
/// FileSystemWatcher / registry watch so the UI re-evaluates when state changes, but
/// <see cref="IsAvailable"/> always reads false until the detection logic lands.
/// </summary>
internal static class EqualizerApoMonitor
{
    // Default 64-bit install location. Equalizer APO ships an x64 installer that lands here.
    // The user can also point us at a custom path via AppSettings (TODO once the settings entry
    // lands) - until then the monitor only watches the default location.
    public const string DefaultInstallDir = @"C:\Program Files\EqualizerAPO";
    public const string ConfiguratorExeName = "Configurator.exe";

    // Sourceforge mirror of the latest x64 installer. Surface in the not-available dialog so the
    // user can grab the EXE in one click. Pinned to a known-good version - chasing 'latest' here
    // would mean parsing Sourceforge's HTML on every flyout open.
    public const string LatestInstallerUrl =
        "https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download";

    // Raised whenever the watcher believes EAPO availability MAY have changed. Listeners must
    // re-read IsAvailable; the event itself carries no payload because state derives from the
    // filesystem + registry, not from a single transition.
    public static event Action? AvailabilityChanged;

    private static readonly Lock InitGate = new();
    private static bool _initialized;
    private static FileSystemWatcher? _dirWatcher;

    /// <summary>
    /// Whether Equalizer APO can be invoked on this system. Stubbed to false until the detection
    /// logic lands - the UI binding path stays correct so flipping this to a real probe later
    /// lights up every device row's button glyph without further wiring.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureWatching();
            // TODO: real detection - File.Exists(Path.Combine(InstallDir, ConfiguratorExeName))
            // AND that the APO COM server is registered. Until then we always report false so the
            // device-row button surfaces the install-or-locate dialog on click.
            return false;
        }
    }

    /// <summary>
    /// Idempotent watcher setup. First read of <see cref="IsAvailable"/> from any device triggers
    /// this; subsequent calls return immediately. Watcher disposal happens at process exit through
    /// the FileSystemWatcher finalizer - we don't expose a Stop method because the monitor is a
    /// process-lifetime singleton.
    /// </summary>
    private static void EnsureWatching()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;
            _initialized = true;
            TryStartDirWatcher();
        }
    }

    private static void TryStartDirWatcher()
    {
        try
        {
            string? parent = Path.GetDirectoryName(DefaultInstallDir);
            if (parent == null || !Directory.Exists(parent)) return;

            _dirWatcher = new FileSystemWatcher(parent)
            {
                Filter = Path.GetFileName(DefaultInstallDir),
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            _dirWatcher.Created += OnFsChange;
            _dirWatcher.Deleted += OnFsChange;
            _dirWatcher.Renamed += OnFsChange;
            _dirWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { WPFLog.Log($"EqualizerApoMonitor.TryStartDirWatcher: {ex.Message}"); }
    }

    private static void OnFsChange(object sender, FileSystemEventArgs e)
    {
        try { AvailabilityChanged?.Invoke(); }
        catch (Exception ex) { WPFLog.Log($"EqualizerApoMonitor.OnFsChange: {ex.Message}"); }
    }

    /// <summary>
    /// Launches the Equalizer APO Configuration Editor scoped to <paramref name="device"/>. Stub -
    /// the real path starts Editor.exe out of the install dir and passes a flag / argument that
    /// pre-selects the endpoint by its IMMDevice id. Bound to ctrl+left-click and right-click on
    /// the device-row equalizer button.
    /// </summary>
    public static void OpenConfigurationEditor(AudioDevice device)
    {
        WPFLog.Log($"EqualizerApoMonitor.OpenConfigurationEditor({device.FriendlyName}): backend not implemented (stub)");
    }
}
