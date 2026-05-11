// Uncomment to pad the tray context menu with 40 dummy device entries.
// Verifies ShowContextMenu positioning when the menu overflows the work area.
// Flip the sibling toggle at the top of VolumeFlyout.xaml.cs to test the flyout too.
#define DEBUG_OVERFLOW_DUMMIES

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.Visuals;
using Point = System.Windows.Point;
using SettingsThemeMode = VolumeTrayAppWPF.Models.ThemeMode;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Tray-app shell. Owns settings, theme, tray icon, hotkeys, the audio device manager, the volume
/// flyout, and the settings window. Software rendering plus custom Win32 interop for the tray icon.
/// </summary>
public partial class App
{
    // Mouse-wheel volume step on the tray icon (percent per notch).
    private const double TrayWheelVolumeStepPercent = 2.0;

    private TrayIconManager? _trayIconManager;
    private TrayIconRenderer? _trayRenderer;
    private AppTheme? _theme;
    private AppSettings? _appSettings;
    private ContextMenu? _contextMenu;
    private CancellationTokenSource? _watcherMonitorCts;
    private SettingsWindow? _settingsWindow;
    private GlobalHotkeyService? _hotkeyService;
    private AudioDeviceManager? _audioManager;
    private VolumeFlyout? _volumeFlyout;
    private AudioDevice? _trackedDevice;

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
        _appSettings.Changed += OnSettingsChanged;
        AppServices.Settings = _appSettings;

        try
        {
            _theme = AppTheme.LoadOrDefault(AppTheme.GetDefaultPath());
            _theme.ThemeChanged += OnThemeChanged;
            AppServices.Theme = _theme;
            UpdateThemeResources(ResolveEffectiveIsLightTheme());
        }
        catch (Exception ex)
        {
            WPFLog.Log($"App.OnStartup: theme init failed: {ex.Message}");
        }

        // Tray-icon renderer ahead of CreateTrayIcon so the first GetTrayIconAndTooltip pass
        // can hand back a real volume-glyph icon instead of a null fallback.
        try
        {
            if (_theme != null) _trayRenderer = new TrayIconRenderer(_theme) { Glyph = GlyphCatalog.PLAYBACK_VOLUME_SILENT };
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: TrayIconRenderer init failed: {ex.Message}"); }

        try { CreateTrayIcon(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: CreateTrayIcon failed: {ex.Message}"); }

        // Audio device manager + the live volume flyout.
        // Wired before the first tray refresh so the icon's initial render reflects the real device state.
        try
        {
            _audioManager = new AudioDeviceManager(Dispatcher, _appSettings);
            _audioManager.PropertyChanged += OnAudioManagerPropertyChanged;
            AttachToTrackedDevice(_audioManager.DefaultDevice);

            _volumeFlyout = new VolumeFlyout(_audioManager);
            _volumeFlyout.FlyoutDeactivated += OnFlyoutDeactivated;
            _volumeFlyout.SettingsRequested += OpenSettings;
        }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: AudioDeviceManager init failed: {ex.Message}"); }

        try { RequestTrayRefresh(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: RequestTrayRefresh failed: {ex.Message}"); }

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

        try { StartWatcherMonitor(); }
        catch (Exception ex) { WPFLog.Log($"App.OnStartup: StartWatcherMonitor failed: {ex.Message}"); }
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
            UpdateThemeResources(ResolveEffectiveIsLightTheme());
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

    /// <summary>
    /// Returns the theme to apply (light=true) after considering the user's ThemeMode override.
    /// </summary>
    private bool ResolveEffectiveIsLightTheme()
    {
        if (_appSettings == null || _theme == null) return _theme?.IsLightTheme ?? false;

        return _appSettings.ThemeMode switch
        {
            SettingsThemeMode.Light => true,
            SettingsThemeMode.Dark => false,
            _ => _theme.IsLightTheme,
        };
    }

    /// <summary>
    /// Polls the watcher process and exits the app when it dies, so we don't run orphaned.
    /// </summary>
    private void StartWatcherMonitor()
    {
        if (Program.WatcherPID is not { } watcherPID) return;

        _watcherMonitorCts = new CancellationTokenSource();
        CancellationToken token = _watcherMonitorCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using Process watcherProcess = Process.GetProcessById(watcherPID);

                while (!token.IsCancellationRequested)
                {
                    if (watcherProcess.HasExited)
                    {
                        await Dispatcher.InvokeAsync(ExitApplication);
                        return;
                    }

                    await Task.Delay(TimeConstants.WatcherLivenessPollIntervalMs, token);
                }
            }
            catch (ArgumentException)
            {
                // Watcher PID already gone - exit immediately.
                await Dispatcher.InvokeAsync(ExitApplication);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation during shutdown.
            }
            catch
            {
                // ignore
            }
        }, token);
    }

    private void CreateTrayIcon()
    {
        if (_theme == null) return;

        _contextMenu = CreateContextMenu();

        _trayIconManager = new TrayIconManager();
        _trayIconManager.IsScrollEnabled = _appSettings?.TrayScrollEnabled ?? true;
        _trayIconManager.LeftMouseDown += OnTrayLeftMouseDown;
        _trayIconManager.LeftClick += OnTrayLeftClick;
        _trayIconManager.LeftDoubleClick += OnTrayLeftDoubleClick;
        _trayIconManager.RightClick += OnTrayRightClick;
        _trayIconManager.RefreshNeeded += RequestTrayRefresh;
        _trayIconManager.Scrolled += OnTrayScrolled;

        RequestTrayRefresh();
        _trayIconManager.IsVisible = true;
    }

    private ContextMenu CreateContextMenu()
    {
        // Items-host (BottomAnchoredItemsPanel) is wired into the ContextMenu Template in App.xaml,
        // not here - the template hard-codes the items host directly (no ItemsPresenter), so a
        // programmatic ContextMenu.ItemsPanel setter has no effect.
        ContextMenu contextMenu = new();

        // Section 1: visible devices, sourced from FlyoutDeviceOrdering so the tray menu and the
        // flyout never disagree on what counts as visible. Same set, same in-flyout order; we just
        // flip top-to-bottom here because the flyout stacks bottom-up with the default at the
        // bottom while a tray menu reads top-down with the default at the top.
        if (_audioManager != null && _appSettings != null)
        {
            List<AudioDevice> orderedForFlyout = FlyoutDeviceOrdering.Build(_audioManager.Devices, _appSettings);
            for (int i = orderedForFlyout.Count - 1; i >= 0; i--)
                contextMenu.Items.Add(BuildDeviceMenuItem(orderedForFlyout[i], _appSettings));
            if (orderedForFlyout.Count > 0) contextMenu.Items.Add(new Separator());
        }

#if DEBUG_OVERFLOW_DUMMIES
        // Pad with 40 dummy entries so the menu overflows the work area.
        for (int debugIndex = 0; debugIndex < 40; debugIndex++)
            contextMenu.Items.Add(new MenuItem { Header = $"Dummy Playback Device {debugIndex + 1:00}" });
        contextMenu.Items.Add(new Separator());
#endif

        MenuItem soundDevicesItem = new() { Header = LocalizationManager.Instance["Tray_SoundDevices"] };
        soundDevicesItem.Click += (_, _) => OpenSoundDevicesPanel();

        MenuItem bluetoothItem = new() { Header = LocalizationManager.Instance["Tray_Bluetooth"] };
        bluetoothItem.Click += (_, _) => OpenBluetoothFlyout();

        MenuItem settingsItem = new() { Header = LocalizationManager.Instance["Tray_Settings"] };
        settingsItem.Click += (_, _) => OpenSettings();

        MenuItem exitItem = new() { Header = LocalizationManager.Instance["Tray_Exit"] };
        exitItem.Click += (_, _) => ExitApplication();

        contextMenu.Items.Add(soundDevicesItem);
        contextMenu.Items.Add(bluetoothItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);

        ApplyContextMenuTheme(contextMenu);

        // Dissolve every Separator:
        // tag the preceding item HasBottomRule (it now paints the 1px rule inside its own ControlTemplate),
        // and the following item HasTopRule (it gets a 2px extra top gap so the rule stays visually centred
        // between pills). The Separator itself is removed.
        // Result: each rule is owned by the adjacent MenuItems' hit-test regions, eliminating the dead band
        // that a real Separator sibling created.
        DissolveSeparatorsIntoNeighbors(contextMenu);

        return contextMenu;
    }

    private const string MenuItemTagHasTopRule = "HasTopRule";
    private const string MenuItemTagHasBottomRule = "HasBottomRule";

    private static void DissolveSeparatorsIntoNeighbors(ContextMenu menu)
    {
        // Walk back-to-front so RemoveAt doesn't shift indices we still need to read.
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not Separator) continue;

            if (i > 0 && menu.Items[i - 1] is MenuItem prev)
                prev.Tag = MenuItemTagHasBottomRule;

            if (i + 1 < menu.Items.Count && menu.Items[i + 1] is MenuItem next)
                next.Tag = MenuItemTagHasTopRule;

            menu.Items.RemoveAt(i);
        }
    }

    private static MenuItem BuildDeviceMenuItem(AudioDevice device, AppSettings settings)
    {
        MenuItem item = new() { Header = FormatTrayMenuDeviceName(device, settings) };
        item.Click += (_, _) => device.SetAsDefault();
        return item;
    }

    // 2-dot ellipsis (".."), per the per-flow tray-menu name spec.
    // Distinct from the Unicode horizontal ellipsis the OS uses elsewhere so the truncation reads
    // as deliberate to anyone who has tuned the max length.
    private const string TrayMenuTruncationSuffix = "..";

    /// <summary>
    /// Picks the slice of <paramref name="device"/>'s name to render in the tray context menu
    /// based on the per-flow style setting, then truncates with a 2-dot ellipsis when the slice
    /// exceeds <see cref="AppSettings.TrayMenuDeviceNameMaxLength"/>.
    /// </summary>
    private static string FormatTrayMenuDeviceName(AudioDevice device, AppSettings settings)
    {
        TrayMenuDeviceNameStyle style = device.IsCaptureDevice
            ? settings.TrayMenuRecordingDeviceNameStyle
            : settings.TrayMenuPlaybackDeviceNameStyle;

        string raw = style switch
        {
            TrayMenuDeviceNameStyle.Name => device.DeviceDescription,
            TrayMenuDeviceNameStyle.Model => device.InterfaceFriendlyName,
            _ => device.FriendlyName,
        };

        if (string.IsNullOrEmpty(raw)) return device.FriendlyName;

        int max = settings.TrayMenuDeviceNameMaxLength;
        if (raw.Length <= max) return raw;

        // Keep room for the suffix inside the cap; if max is smaller than the suffix itself we
        // degrade to a hard cut at max chars rather than producing a string longer than requested.
        int keep = Math.Max(0, max - TrayMenuTruncationSuffix.Length);
        return keep == 0 ? raw[..Math.Min(raw.Length, max)] : raw[..keep] + TrayMenuTruncationSuffix;
    }

    // Opens the classic Sound control panel on the Playback tab via mmsys.cpl.
    // Other valid panel names: "recording", "sounds", "communications".
    private static void OpenSoundDevicesPanel()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,playback",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { WPFLog.Log($"App.OpenSoundDevicesPanel: {ex.Message}"); }
    }

    // Opens the Windows 11 Quick Settings Bluetooth flyout via the ms-actioncenter URI.
    // Launched through explorer.exe so the URI handler resolves consistently across builds.
    private static void OpenBluetoothFlyout()
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "ms-actioncenter:controlcenter/bluetooth",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { WPFLog.Log($"App.OpenBluetoothFlyout: {ex.Message}"); }
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
        AudioDevice? device = _trackedDevice;
        if (device == null) return;

        double currentPercent = device.Volume * 100.0;
        double next = currentPercent + (delta > 0 ? TrayWheelVolumeStepPercent : -TrayWheelVolumeStepPercent);
        if (next < 0) next = 0;
        else if (next > 100) next = 100;
        device.Volume = (float)(next / 100.0);
    }

    private void OnFlyoutDeactivated()
    {
        // Flyout already calls Hide() on deactivate; nothing further to do here today, but the hook
        // is the natural seat for a future "remember last opened device" or telemetry-style behavior.
    }

    private void OnAudioManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioDeviceManager.DefaultDevice))
            AttachToTrackedDevice(_audioManager?.DefaultDevice);
    }

    private void AttachToTrackedDevice(AudioDevice? device)
    {
        if (ReferenceEquals(_trackedDevice, device)) return;

        if (_trackedDevice != null)
            _trackedDevice.PropertyChanged -= OnTrackedDevicePropertyChanged;

        _trackedDevice = device;

        if (_trackedDevice != null)
            _trackedDevice.PropertyChanged += OnTrackedDevicePropertyChanged;

        RequestTrayRefresh();
    }

    private void OnTrackedDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Master volume / mute changes are the only mutations that affect the tray icon glyph or
        // tooltip; ignore the rest (PeakValue, FriendlyName, IsDefault) so the throttled refresh
        // path doesn't fire on every meter tick.
        if (e.PropertyName == nameof(AudioDevice.Volume) ||
            e.PropertyName == nameof(AudioDevice.IsMuted))
        {
            RequestTrayRefresh();
        }
    }

    private void OnTrayRightClick(Point point)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Rebuild every time so settings-driven changes take effect.
            _contextMenu = CreateContextMenu();
            ContextMenuPosition placement = _appSettings?.ContextMenuPosition ?? ContextMenuPosition.Classic;
            _trayIconManager?.ShowContextMenu(_contextMenu, point, placement);
        });
    }

    private void OnThemeChanged(bool isLightTheme)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ResolveEffectiveIsLightTheme();
            UpdateThemeResources(effective);
            if (_trayIconManager != null) RequestTrayRefresh();
        });
    }

    private void OnSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool effective = ResolveEffectiveIsLightTheme();
            UpdateThemeResources(effective);

            if (_trayIconManager != null && _appSettings != null)
            {
                _trayIconManager.IsScrollEnabled = _appSettings.TrayScrollEnabled;
                RequestTrayRefresh();
            }

            // Re-apply hotkeys so edits in Settings take effect immediately.
            if (_hotkeyService != null && _appSettings != null) _hotkeyService.Apply(_appSettings.Hotkeys);

            _contextMenu = CreateContextMenu();
        });
    }

    private void ApplyContextMenuTheme(ContextMenu menu)
    {
        if (_theme == null) return;

        bool isLight = ResolveEffectiveIsLightTheme();

        menu.Background = new SolidColorBrush(_theme.ResolveBackground(_appSettings, isLight));
        menu.Foreground = new SolidColorBrush(_theme.ResolveForeground(_appSettings, isLight));
        menu.BorderBrush = new SolidColorBrush(_theme.Border.For(isLight));

        int fontSize = _appSettings?.ContextMenuFontSize ?? 15;

        foreach (object item in menu.Items)
        {
            switch (item)
            {
                case MenuItem menuItem:
                    menuItem.Foreground = menu.Foreground;
                    menuItem.FontSize = fontSize;
                    break;
                case Separator separator:
                    separator.Background = new SolidColorBrush(_theme.Separator.For(isLight));
                    break;
            }
        }
    }

    private void UpdateThemeResources(bool isLightTheme)
    {
        if (_theme == null) return;

        // Core colors (user overrides win).
        Resources["ThemeBackground"] = new SolidColorBrush(_theme.ResolveBackground(_appSettings, isLightTheme));
        Resources["ThemeForeground"] = new SolidColorBrush(_theme.ResolveForeground(_appSettings, isLightTheme));
        Resources["ThemeBorder"] = new SolidColorBrush(_theme.Border.For(isLightTheme));
        Resources["ThemeHover"] = new SolidColorBrush(_theme.Hover.For(isLightTheme));
        Resources["ThemePressed"] = new SolidColorBrush(_theme.Pressed.For(isLightTheme));
        Resources["ThemeSeparator"] = new SolidColorBrush(_theme.Separator.For(isLightTheme));
        Resources["ThemeDisabledForeground"] = new SolidColorBrush(_theme.DisabledForeground.For(isLightTheme));
        Resources["ThemeAccent"] = new SolidColorBrush(_theme.Accent.For(isLightTheme));

        Resources["ThemeSecondaryForeground"] = new SolidColorBrush(_theme.SecondaryForeground.For(isLightTheme));
        Resources["ThemeFooterBackground"] = new SolidColorBrush(_theme.FooterBackground.For(isLightTheme));

        // Win11 Settings card background (slightly lighter than body).
        Resources["ThemeCardBackground"] = new SolidColorBrush(_theme.CardBackground.For(isLightTheme));

        // Win11 input control background (text boxes, combo boxes, buttons).
        Resources["ThemeControlBackground"] = new SolidColorBrush(_theme.ControlBackground.For(isLightTheme));

        // Focused TextBox: a shade darker than ThemeControlBackground so the focused state stays visible
        // without collapsing toward the window bg.
        Resources["ThemeTextBoxFocused"] = new SolidColorBrush(_theme.TextBoxFocused.For(isLightTheme));
        Resources["ThemeSliderTrack"] = new SolidColorBrush(_theme.SliderTrack.For(isLightTheme));
        Resources["ThemeSliderProgress"] = new SolidColorBrush(_theme.SliderProgress.For(isLightTheme));
        Resources["ThemeSliderThumb"] = new SolidColorBrush(_theme.SliderThumb.For(isLightTheme));

        // Peak meter overlays. Two solid colors (no light/dark variant), both live-preview-aware
        // so the picker drag updates each brush in real time before the user closes the picker.
        // MeterPeakBrush paints the base bar to min(L, R); MeterPeakStereoBrush paints on top to
        // max(L, R) and is translucent by default so the stereo extension reads as a halo.
        // Fallbacks match the *Default consts in AppSettings.
        System.Windows.Media.Color meterPeakColor =
            _appSettings?.EffectiveMeterPeakColor ?? System.Windows.Media.Colors.White;
        Resources["MeterPeakBrush"] = new SolidColorBrush(meterPeakColor);
        System.Windows.Media.Color meterPeakStereoColor =
            _appSettings?.EffectiveMeterPeakStereoColor
            ?? System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        Resources["MeterPeakStereoBrush"] = new SolidColorBrush(meterPeakStereoColor);
        Resources["ThemeButtonHover"] = new SolidColorBrush(_theme.ButtonHover.For(isLightTheme));
        Resources["ThemeButtonPressed"] = new SolidColorBrush(_theme.ButtonPressed.For(isLightTheme));
        Resources["ThemeIconForeground"] = new SolidColorBrush(_theme.IconForeground.For(isLightTheme));

        // Chrome brushes for control templates whose triggers used to hardcode hex literals in App.xaml.
        // These are theme-agnostic single-color values
        // promoting to per-theme is a one-line lift (.For(isLightTheme)) if visual design ever requires it.
        Resources["ToggleSwitchOnTrackBrush"] = new SolidColorBrush(_theme.ToggleSwitchOnTrack.Light);
        Resources["ToggleSwitchOnThumbBrush"] = new SolidColorBrush(_theme.ToggleSwitchOnThumb.Light);
        Resources["CloseButtonHoverBrush"] = new SolidColorBrush(_theme.CloseButtonHover.Light);
        Resources["CloseButtonPressedBrush"] = new SolidColorBrush(_theme.CloseButtonPressed.Light);
        Resources["CloseButtonGlyphActiveBrush"] = new SolidColorBrush(_theme.CloseButtonGlyphActive.Light);

        Resources["GlyphSettings"] = _theme.GlyphSettings;

        // Rounded-corners toggle:
        // map every literal radius in XAML to a resource that evaluates to 0 when disabled,
        // and the original visual value when on.
        bool rounded = _appSettings?.EnableRoundedCorners ?? true;
        Resources["CornerRadiusTiny"] = new CornerRadius(rounded ? 1.5 : 0);
        Resources["CornerRadiusSmall"] = new CornerRadius(rounded ? 2 : 0);
        Resources["CornerRadiusScrollThumb"] = new CornerRadius(rounded ? 3 : 0);
        Resources["CornerRadiusScrollThumbExpanded"] = new CornerRadius(rounded ? 7 : 0);
        Resources["CornerRadiusMedium"] = new CornerRadius(rounded ? 4 : 0);
        Resources["CornerRadiusLarge"] = new CornerRadius(rounded ? 6 : 0);
        Resources["CornerRadiusFlyout"] = new CornerRadius(rounded ? 8 : 0);
        Resources["CornerRadiusHuge"] = new CornerRadius(rounded ? 16 : 0);
        Resources["CornerRadiusFooterBottom"] = new CornerRadius(0, 0, rounded ? 8 : 0, rounded ? 8 : 0);

        // Slider thumb: resolve the user's selected option (or default) and push every part of its
        // shape onto DynamicResource keys the flyout's thumb template binds against.
        SliderThumbGlyphOption thumb = ResolveSliderThumbOption();
        Resources["SliderThumbGlyph"] = thumb.Glyph;
        Resources["SliderThumbGlyphFont"] = new System.Windows.Media.FontFamily(thumb.FontFamily);
        Resources["SliderThumbGlyphSize"] = thumb.FontSize;
        Resources["SliderThumbGlyphWidth"] = thumb.Width;
        Resources["SliderThumbGlyphHeight"] = thumb.Height;
        Resources["SliderThumbGlyphScaleX"] = thumb.XScale;
        Resources["SliderThumbGlyphVisibility"] = thumb.IsGlyph ? Visibility.Visible : Visibility.Collapsed;
        Resources["SliderThumbCapsuleVisibility"] = thumb.IsCapsule ? Visibility.Visible : Visibility.Collapsed;

        // Capsule corner radius = half the smaller dimension -> semicircular ends, straight sides.
        // Border.CornerRadius doesn't auto-clamp, so an over-large value renders as a lens, not a pill.
        // Goes to 0 when the user disables rounded corners - same Border then serves as a sharp bar.
        double capsuleRadius = rounded ? Math.Min(thumb.Width, thumb.Height) / 2.0 : 0;
        Resources["CornerRadiusCapsule"] = new CornerRadius(capsuleRadius);
    }

    private SliderThumbGlyphOption ResolveSliderThumbOption()
    {
        List<SliderThumbGlyphOption> options =
            _appSettings?.SliderThumbOptions is { Count: > 0 } list
                ? list
                : SliderThumbGlyphOption.CreateDefaults();

        string name = _appSettings?.SliderThumbGlyph ?? "Capsule";
        return options.FirstOrDefault(o => o.Name == name) ?? options[0];
    }

    private void RequestTrayRefresh() => _trayIconManager?.Update(GetTrayIconAndTooltip);

    private (Icon? icon, string tooltip) GetTrayIconAndTooltip()
    {
        AudioDevice? device = _trackedDevice;
        bool isLight = ResolveEffectiveIsLightTheme();

        if (_trayRenderer == null)
        {
            // Renderer not ready yet (theme load failed during startup); fall back to a tooltip-only update.
            return (null, "Volume");
        }

        if (device == null)
        {
            _trayRenderer.IsLightTheme = isLight;
            _trayRenderer.Glyph = GlyphCatalog.PLAYBACK_VOLUME_SILENT;
            _trayRenderer.BackdropGlyph = GlyphCatalog.PLAYBACK_VOLUME_HIGH;
            return (_trayRenderer.CreateIcon(), "No audio device");
        }

        _trayRenderer.IsLightTheme = isLight;
        string foregroundGlyph = GlyphCatalog.GetVolumeTier(device.Volume, device.IsMuted);
        _trayRenderer.Glyph = foregroundGlyph;
        // Full-volume speaker as a dimmed backdrop on every partial state, mirroring how the OS
        // shell paints Wi-Fi: the silhouette of the full glyph stays present so the partial
        // foreground reads as "this much of that". Skip the backdrop when the foreground IS
        // the full glyph - drawing it twice would just darken the icon.
        _trayRenderer.BackdropGlyph = foregroundGlyph == GlyphCatalog.PLAYBACK_VOLUME_HIGH ? null : GlyphCatalog.PLAYBACK_VOLUME_HIGH;

        int percent = (int)Math.Round(device.Volume * 100);
        string tooltip = device.IsMuted
            ? $"{device.FriendlyName}: muted"
            : $"{device.FriendlyName}: {percent}%";
        return (_trayRenderer.CreateIcon(), tooltip);
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
            try { _hotkeyService.Dispose(); } catch { /* ignore */ }
            _hotkeyService = null;
        }

        _watcherMonitorCts?.Cancel();
        _watcherMonitorCts?.Dispose();
        _watcherMonitorCts = null;

        if (_appSettings != null) _appSettings.Changed -= OnSettingsChanged;

        // Close child windows; unsubscribe handlers first so they don't fire mid-shutdown.
        if (_settingsWindow != null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            try { _settingsWindow.Close(); } catch { /* ignore */ }
            _settingsWindow = null;
        }

        if (_theme != null)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _theme.Dispose();
            _theme = null;
        }

        if (_volumeFlyout != null)
        {
            _volumeFlyout.FlyoutDeactivated -= OnFlyoutDeactivated;
            _volumeFlyout.SettingsRequested -= OpenSettings;
            try { _volumeFlyout.Close(); } catch { /* ignore */ }
            _volumeFlyout = null;
        }

        if (_trackedDevice != null)
        {
            _trackedDevice.PropertyChanged -= OnTrackedDevicePropertyChanged;
            _trackedDevice = null;
        }

        if (_audioManager != null)
        {
            _audioManager.PropertyChanged -= OnAudioManagerPropertyChanged;
            try { _audioManager.Dispose(); } catch { /* ignore */ }
            _audioManager = null;
        }

        if (_trayIconManager != null)
        {
            _trayIconManager.LeftMouseDown -= OnTrayLeftMouseDown;
            _trayIconManager.LeftClick -= OnTrayLeftClick;
            _trayIconManager.LeftDoubleClick -= OnTrayLeftDoubleClick;
            _trayIconManager.RightClick -= OnTrayRightClick;
            _trayIconManager.RefreshNeeded -= RequestTrayRefresh;
            _trayIconManager.Scrolled -= OnTrayScrolled;
            _trayIconManager.Dispose();
            _trayIconManager = null;
        }

        _trayRenderer?.Dispose();
        _trayRenderer = null;

        _contextMenu = null;

        WPFLog.Log("App.ExitApplication: clean exit");
        WPFLog.Flush();
        Shutdown(0);
    }
}
