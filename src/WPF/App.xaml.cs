using System.IO;
using System.Windows;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.Utils;
using VolumeTrayAppWPF.Visuals;
using Point = System.Windows.Point;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Tray-app shell. Owns settings, theme, the audio device manager, the tray shell, the volume
/// flyout, and the settings window. Heavy lifting lives in:
///   <see cref="TrayShell"/>          - tray icon + renderer + audio tracking
///   <see cref="TrayContextMenu"/>    - right-click menu construction
///   <see cref="ThemeResources"/>     - resource-dictionary push on theme/settings change
///   <see cref="WatcherMonitor"/>     - watcher-process liveness poll
/// </summary>
public partial class App
{
    private AppTheme? _theme;
    private AppSettings? _appSettings;
    private TrayShell? _trayShell;
    private System.Windows.Controls.ContextMenu? _contextMenu;
    private SettingsWindow? _settingsWindow;
    private GlobalHotkeyService? _hotkeyService;
    private AudioDeviceManager? _audioManager;
    private VolumeFlyout? _volumeFlyout;
    private WatcherMonitor? _watcherMonitor;

    // Suppresses the LeftClick that follows a LeftMouseDown that auto-hid the flyout,
    // so clicking the tray icon while the flyout is open closes it without immediately reopening.
    private bool _suppressNextTrayClick;


    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Idempotent - Program.Main already called this.
        // Safe to repeat so any direct App entry (e.g. attached debugger) still gets a logger.
        WPFLog.Initialize();
        WPFLog.Log($"App.OnStartup: begin, args=[{string.Join(' ', e.Args)}]");

        // Seed the localization manager before any UI is built so the first XAML load
        // sees the right culture on every {loc:Loc ...} lookup.
        LocalizationManager.Instance.Initialize();

        if (Program.IsUninstallerMode)
        {
            RunUninstallerMode();
            return;
        }

        WireCrashHandlers();
        LoadSettingsAndAutostart();
        LoadThemeAndApplyResources();

        // Audio device manager + the live volume flyout.
        // Built before the tray shell so the first GetTrayIconAndTooltip pass sees a real device.
        try
        {
            if (_appSettings != null)
            {
                _audioManager = new AudioDeviceManager(Dispatcher, _appSettings);
                _volumeFlyout = new VolumeFlyout(_audioManager);
                _volumeFlyout.FlyoutDeactivated += OnFlyoutDeactivated;
                _volumeFlyout.SettingsRequested += OpenSettings;
            }
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: AudioDeviceManager init failed: {ex.Message}"); }

        try { CreateTrayShell(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: CreateTrayShell failed: {ex.Message}"); }

        // Global hotkeys. Owns its own message-only window for WM_HOTKEY;
        // created on the UI thread so RegisterHotKey's thread-affinity contract is satisfied
        // and hotkey events fire back here without Dispatcher marshaling.
        try
        {
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.Initialize();
            _hotkeyService.Fired += OnHotkeyFired;
            if (_appSettings != null) _hotkeyService.Apply(_appSettings.Hotkeys);

            AppServices.HotkeyService = _hotkeyService;
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: GlobalHotkeyService init failed: {ex.Message}"); }

        try
        {
            _watcherMonitor = new WatcherMonitor(Dispatcher, ExitApplication);
            _watcherMonitor.Start();
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: WatcherMonitor start failed: {ex.Message}"); }
    }

    private void WireCrashHandlers()
    {
        // Crash-path shutdown handlers. Cap each best-effort cleanup so a hung op can't block the exit.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WPFLog.Log($"FATAL UnhandledException: {args.ExceptionObject}");
            WPFLog.Flush();
            Environment.Exit(1);
        };
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            WPFLog.Log($"FATAL DispatcherUnhandledException: {args.Exception}");
            WPFLog.Flush();
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // Last handler to fire on every exit path - tear the logger down here, not earlier.
            WPFLog.Shutdown();
        };
        SessionEnding += (_, args) =>
        {
            WPFLog.Log($"SessionEnding: reason={args.ReasonSessionEnding}");
            WPFLog.Flush();
        };
    }

    private void LoadSettingsAndAutostart()
    {
        // Detect first-run before LoadOrDefault writes the default file
        // so we can reconcile OS state (e.g. startup registration) with the defaults that just got persisted.
        try
        {
            string settingsPath = AppSettings.GetDefaultPath();
            bool firstRun = !File.Exists(settingsPath);
            _appSettings = AppSettings.LoadOrDefault(settingsPath);
            if (firstRun) StartupManager.SetRunOnStartup(_appSettings.RunOnStartup);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: settings load failed: {ex.Message}");
            _appSettings = new AppSettings();
        }

        // Drop the legacy HKCU\...\Run autostart entry (older builds wrote one)
        // and revalidate the shell:startup shortcut.
        // Without these, an upgraded user could end up running the app twice at sign-in,
        // or worse, having the shortcut point at a no-longer-existing exe path that silently does nothing.
        StartupManager.RemoveLegacyRunKey();
        StartupManager.RepairShortcutIfStale();
        _appSettings!.Changed += OnSettingsChanged;
        AppServices.Settings = _appSettings;

        // Per-device persisted UI state (drawer expand/collapse, etc). Lives in devices.xml next to
        // settings.xml; failures here fall back to an empty in-memory collection so the rest of the
        // app still works -- worst case the user loses persisted drawer state for this session.
        try
        {
            AppServices.DeviceSettings = DeviceSettings.LoadOrDefault();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: device settings load failed: {ex.Message}");
            AppServices.DeviceSettings = new DeviceSettings();
        }
    }

    private void LoadThemeAndApplyResources()
    {
        try
        {
            _theme = AppTheme.LoadOrDefault(AppTheme.GetDefaultPath());
            _theme.ThemeChanged += OnThemeChanged;
            AppServices.Theme = _theme;
            ThemeResources.Apply(this, _theme, _appSettings,
                ThemeResources.ResolveEffectiveIsLightTheme(_appSettings, _theme));
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: theme init failed: {ex.Message}");
        }
    }

    private void CreateTrayShell()
    {
        if (_theme == null || _appSettings == null || _audioManager == null) return;

        _trayShell = new TrayShell(_theme, _appSettings, _audioManager);
        _trayShell.LeftMouseDown += OnTrayLeftMouseDown;
        _trayShell.LeftClick += OnTrayLeftClick;
        _trayShell.LeftDoubleClick += OnTrayLeftDoubleClick;
        _trayShell.RightClick += OnTrayRightClick;
        _trayShell.Scrolled += OnTrayScrolled;

        _trayShell.Show();
    }

    /// <summary>
    /// Stripped-down startup for <c>--uninstall</c> mode:
    /// load settings purely so the theme follows the user's preference, init theme resources,
    /// then show <see cref="UninstallerWindow"/> as the only window.
    /// No tray icon, no hotkeys, no watcher.
    /// </summary>
    private void RunUninstallerMode()
    {
        try { _appSettings = AppSettings.LoadOrDefault(); }
        catch { _appSettings = new AppSettings(); }

        try
        {
            _theme = AppTheme.LoadOrDefault(AppTheme.GetDefaultPath());
            ThemeResources.Apply(this, _theme, _appSettings,
                ThemeResources.ResolveEffectiveIsLightTheme(_appSettings, _theme));
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.RunUninstallerMode: theme init failed: {ex.Message}");
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        UninstallerWindow window = new(
            Program.UninstallerInstallDir ?? string.Empty,
            Program.UninstallerScope);
        MainWindow = window;
        window.Show();
    }

    private void OnHotkeyFired(object? sender, HotkeyFiredEventArgs e)
    {
        try { HandleHotkey(e.Action); }
        catch (Exception ex) { WPFLog.Log($"App.OnHotkeyFired: {ex.Message}"); }
    }

    /// <summary>
    /// Translates a fired hotkey into the matching app action.
    /// Runs on the UI thread (WM_HOTKEY arrives on the message-only window's thread,
    /// which we created on the UI thread), so direct calls into UI services are safe.
    /// Skeleton wires only the generic actions; brightness / monitor / night-light handlers
    /// are reintroduced by the hosting app.
    /// </summary>
    private void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.OpenSettings:
                OpenSettings();
                break;
        }
    }

    /// <summary>
    /// LBUTTONDOWN fires before the flyout's WM_KILLFOCUS reaches it - check the flyout's foreground state
    /// here so a click on the tray icon while the flyout is open dismisses it without flickering through
    /// a re-show on the trailing LeftClick.
    /// Skip the dismiss path when the flyout is undocked: undocked flyouts don't auto-hide on focus loss,
    /// and a tray click should redock them rather than hide them. The trailing LeftClick handles that
    /// path via <see cref="ShowVolumeFlyout"/>, which calls <see cref="VolumeFlyout.Redock"/>.
    /// </summary>
    private void OnTrayLeftMouseDown()
    {
        if (_volumeFlyout is { IsUndocked: false } && _volumeFlyout.HasFocus())
        {
            _volumeFlyout.Hide();
            _suppressNextTrayClick = true;
        }
    }

    private void OnTrayLeftClick()
    {
        if (_suppressNextTrayClick) { _suppressNextTrayClick = false; return; }
        ShowVolumeFlyout();
    }

    private void OnTrayLeftDoubleClick()
    {
        if (_suppressNextTrayClick) { _suppressNextTrayClick = false; return; }
        ShowVolumeFlyout();
    }

    private void ShowVolumeFlyout()
    {
        if (_volumeFlyout == null) return;

        // Tray click always anchors the flyout back at the tray corner: redock first so an undocked
        // window doesn't keep its floating position when the user expected the tray-anchored popup.
        // The saved undocked position is preserved so the next press of the undock button restores
        // the user's manual placement.
        _volumeFlyout.Redock();
        _volumeFlyout.Show();
    }

    /// <summary>
    /// Mouse wheel over the tray icon adjusts the default device's master volume.
    /// Step matches the in-flyout slider scroll for a consistent feel between the two surfaces.
    /// </summary>
    private void OnTrayScrolled(int delta)
    {
        AudioDevice? device = _trayShell?.TrackedDevice;
        if (device == null) return;

        double currentPercent = device.Volume * 100.0;
        double step = AppConstants.WheelVolumeStepPercent;
        double next = currentPercent + (delta > 0 ? step : -step);
        if (next < 0) next = 0;
        else if (next > 100) next = 100;
        device.Volume = (float)(next / 100.0);
    }

    private void OnFlyoutDeactivated()
    {
        // Flyout already calls Hide() on deactivate; nothing further to do here today, but the hook
        // is the natural seat for a future "remember last opened device" or telemetry-style behavior.
    }

    private void OnTrayRightClick(Point point)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Rebuild every time so settings-driven changes take effect.
            _contextMenu = TrayContextMenu.Build(_audioManager, _appSettings, OpenSettings, ExitApplication);
            ContextMenuPosition placement = _appSettings?.ContextMenuPosition ?? ContextMenuPosition.Classic;
            _trayShell?.ShowContextMenu(_contextMenu, point, placement);
        });
    }

    private void OnThemeChanged(bool isLightTheme)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ThemeResources.ResolveEffectiveIsLightTheme(_appSettings, _theme);
            ThemeResources.Apply(this, _theme, _appSettings, effective);
            _trayShell?.RequestRefresh();
        });
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ThemeResources.ResolveEffectiveIsLightTheme(_appSettings, _theme);
            ThemeResources.Apply(this, _theme, _appSettings, effective);

            _trayShell?.ApplySettings();

            // Re-apply hotkeys so edits in Settings take effect immediately.
            if (_hotkeyService != null && _appSettings != null) _hotkeyService.Apply(_appSettings.Hotkeys);

            // Rebuild the cached context menu so the next right-click reflects fresh device names / order.
            _contextMenu = TrayContextMenu.Build(_audioManager, _appSettings, OpenSettings, ExitApplication);
        });
    }

    private void OpenSettings()
    {
        if (_appSettings == null) return;

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_appSettings);
            _settingsWindow.Closed += OnSettingsWindowClosed;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow = null;
        }

        // Aggressive GC after the heavy settings UI is torn down
        // to reclaim memory that would otherwise linger in gen2 for a long-running tray app.
        _ = Task.Delay(TimeConstants.PostSettingsCloseGCDelayMs).ContinueWith(_ =>
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, TaskScheduler.Default);
    }

    private void ExitApplication()
    {
        // Tear down the global hotkey service first to unregister all WM_HOTKEY bindings
        // so they can't fire into an app that's mid-shutdown.
        if (_hotkeyService != null)
        {
            _hotkeyService.Fired -= OnHotkeyFired;
            Safe.Dispose(_hotkeyService);
            _hotkeyService = null;
        }

        Safe.Dispose(_watcherMonitor);
        _watcherMonitor = null;

        if (_appSettings != null) _appSettings.Changed -= OnSettingsChanged;

        // Close child windows; unsubscribe handlers first so they don't fire mid-shutdown.
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            try { _settingsWindow.Close(); }
            catch (Exception ex) { WPFLog.Log($"App.ExitApplication: settings window close: {ex.Message}"); }
            _settingsWindow = null;
        }

        if (_theme != null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            Safe.Dispose(_theme);
            _theme = null;
        }

        if (_volumeFlyout != null)
        {
            _volumeFlyout.FlyoutDeactivated -= OnFlyoutDeactivated;
            _volumeFlyout.SettingsRequested -= OpenSettings;
            try { _volumeFlyout.Close(); }
            catch (Exception ex) { WPFLog.Log($"App.ExitApplication: flyout close: {ex.Message}"); }
            _volumeFlyout = null;
        }

        if (_audioManager != null)
        {
            Safe.Dispose(_audioManager);
            _audioManager = null;
        }

        Safe.Dispose(_trayShell);
        _trayShell = null;

        _contextMenu = null;

        WPFLog.Log("App.ExitApplication: clean exit");
        WPFLog.Flush();
        Shutdown(0);
    }
}
