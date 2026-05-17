using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Visuals;
using VolumeTrayAppWPF.WPF.Utils;
using RadioButton = System.Windows.Controls.RadioButton;

namespace VolumeTrayAppWPF.WPF;

public enum SettingsTab
{
    General,
    Flyout,
    DeviceAppDrawers,
    TrayIcon,
    Devices,
    Hotkeys,
    Theme,
    About,
}


public partial class SettingsWindow : Window
{
    // Per-page convention. Pages that need to refresh state on navigate (HotkeysPage rows,
    // GeneralPage install-status) expose a parameterless instance method named "RefreshOnShow"
    // and SettingsWindow invokes it via reflection in NavItem_Checked. No base class / interface;
    // the convention keeps the page list and the shell decoupled.
    private const string RefreshOnShowMethodName = "RefreshOnShow";

    private readonly AppSettings _settings;

    private HwndSource? _hwndSource;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadFromSettings();

        ApplyOuterCornerRadius();

        _settings.Changed += OnAppSettingsChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Closed += OnWindowClosed;

        // Land keyboard focus on the currently-checked nav item once the visual tree is realized,
        // so arrow nav works immediately when the window opens.
        ContentRendered += (_, _) =>
        {
            RadioButton? checkedNav = new[] {
                NavGeneral, NavFlyout, NavDeviceAppDrawers, NavTrayIcon, NavDevices, NavHotkeys, NavTheme, NavAbout
            }.FirstOrDefault(rb => rb.IsChecked == true);
            checkedNav?.Focus();
        };
    }

    private void OnAppSettingsChanged() => Dispatcher.BeginInvoke(ApplyOuterCornerRadius);

    /// <summary>
    /// Returns true when this window's HWND is the OS-level foreground window.
    /// Mirrors <see cref="VolumeFlyout.HasFocus"/> - used by the flyout's deactivation handler
    /// to tell whether focus is moving to settings (keep flyout open) versus to an unrelated window (hide flyout).
    /// </summary>
    public bool HasFocus()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && hwnd == User32.GetForegroundWindow();
    }

    /// <summary>
    /// Pair the flyout's visibility with this window's focus:
    /// bring it back when settings is activated, hide it when settings is deactivated.
    /// The flyout is opened via <see cref="VolumeFlyout.ShowWithoutActivating"/> so it doesn't steal focus
    /// from settings (which would immediately trigger the deactivation hide and flicker).
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            if (window is VolumeFlyout { IsVisible: false } flyout)
            {
                flyout.ShowWithoutActivating();
                break;
            }
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        VolumeFlyout? flyout = null;
        foreach (Window window in System.Windows.Application.Current.Windows)
            if (window is VolumeFlyout { IsVisible: true } f) { flyout = f; break; }

        if (flyout == null) return;

        // Undocked flyout is a free-floating user-positioned window; it isn't paired to settings' visibility,
        // so closing settings (or just clicking elsewhere) must not pull it offscreen.
        if (flyout.IsUndocked) return;

        // Fast path:
        // the OS foreground HWND has already settled on the flyout (user clicked the flyout itself) - nothing to do.
        if (flyout.HasFocus()) return;

        // Otherwise the activation transition is still in flight.
        // Race the flyout's Activated event against a single Input-priority dispatcher tick.
        // Input runs after all WM_ACTIVATE currently queued,
        // so by then Activated would have fired if focus was going to land on the flyout.
        // Whichever signal arrives first decides - no Background-priority idle wait,
        // no risk of getting wedged behind unrelated dispatcher work.
        bool keep = false;
        EventHandler? onActivated = null;
        onActivated = (_, _) =>
        {
            flyout.Activated -= onActivated;
            keep = true;
        };
        flyout.Activated += onActivated;

        Dispatcher.BeginInvoke(() =>
        {
            flyout.Activated -= onActivated;
            if (!keep && !flyout.HasFocus()) flyout.Hide();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Drives the outer window chrome + root border corner radius (and DWM corner preference)
    /// from AppSettings. Delegates to <see cref="ChromeCornerRadiusHelper.Apply"/> so the picker
    /// window shares the same chrome plumbing without duplicating the WindowChrome / Border / DWM
    /// fan-out.
    /// </summary>
    private void ApplyOuterCornerRadius()
    {
        double r = _settings.EnableRoundedCorners ? 8 : 0;
        ChromeCornerRadiusHelper.Apply(this, RootBorder, r);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WindowProcHook);
            _hwndSource = null;
        }

        _settings.Changed -= OnAppSettingsChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyDWMDarkMode(ThemeResources.ResolveEffectiveIsLightTheme(_settings, AppServices.Theme));

        // HWND exists now, so the DWM corner-preference call inside the helper actually lands.
        // The constructor's earlier ApplyOuterCornerRadius was a layout-only pre-pass.
        ApplyOuterCornerRadius();
        UpdateMaximizeGlyph();

        // Force MA_ACTIVATE on inactive-window clicks so the first click that activates the SettingsWindow
        // ALSO reaches WPF input. Without this, custom-chrome modeless windows can occasionally see the
        // OS return MA_ACTIVATEANDEAT, which swallows the click.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowProcHook);
        }
    }

    private IntPtr WindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(User32.MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Re-evaluate the effective light/dark state and push the DWM immersive-dark-mode attribute
    /// onto this window's HWND. Safe to call before SourceInitialized - short-circuits then.
    /// Replaces the deleted <c>IThemeHost</c> interface; <see cref="Settings.Pages.ThemePage"/>
    /// reaches the host through <c>Window.GetWindow(this) is SettingsWindow</c> instead.
    /// </summary>
    public void ApplyDWMDarkMode(bool isLight)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Dark title bar on Win11; matching the app's effective theme.
            int value = isLight ? 0 : 1;
            DWMAPI.DwmSetWindowAttribute(hwnd, DWMAPI.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            // DWM call may fail on older Windows; non-fatal.
            WPFLog.Log($"SettingsWindow.ApplyDWMDarkMode: {ex.Message}");
        }
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized
            ? GlyphCatalog.CHROME_RESTORE
            : GlyphCatalog.CHROME_MAXIMIZE;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void SelectTab(SettingsTab tab)
    {
        RadioButton? target = tab switch
        {
            SettingsTab.General => NavGeneral,
            SettingsTab.Flyout => NavFlyout,
            SettingsTab.DeviceAppDrawers => NavDeviceAppDrawers,
            SettingsTab.TrayIcon => NavTrayIcon,
            SettingsTab.Devices => NavDevices,
            SettingsTab.Hotkeys => NavHotkeys,
            SettingsTab.Theme => NavTheme,
            SettingsTab.About => NavAbout,
            _ => null,
        };
        if (target != null) target.IsChecked = true;
    }

    private void LoadFromSettings()
    {
        // Per-section pages own their own load - see <Page>.LoadFromSettings.
        GeneralSection.LoadFromSettings(_settings);
        FlyoutSection.LoadFromSettings(_settings);
        DeviceAppDrawersSection.LoadFromSettings(_settings);
        TrayIconSection.LoadFromSettings(_settings);
        DevicesSection.LoadFromSettings(_settings);
        HotkeysSection.LoadFromSettings(_settings);
        ThemeSection.LoadFromSettings(_settings, this);
        AboutSection.LoadFromSettings(_settings);
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.GetDirectoryName(AppSettings.GetDefaultPath()) ?? string.Empty;
        if (string.IsNullOrEmpty(folder)) return;

        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        GeneralSection.Visibility = Visibility.Collapsed;
        FlyoutSection.Visibility = Visibility.Collapsed;
        DeviceAppDrawersSection.Visibility = Visibility.Collapsed;
        TrayIconSection.Visibility = Visibility.Collapsed;
        DevicesSection.Visibility = Visibility.Collapsed;
        HotkeysSection.Visibility = Visibility.Collapsed;
        ThemeSection.Visibility = Visibility.Collapsed;
        AboutSection.Visibility = Visibility.Collapsed;

        System.Windows.Controls.UserControl? activated = tag switch
        {
            "General" => GeneralSection,
            "Flyout" => FlyoutSection,
            "DeviceAppDrawers" => DeviceAppDrawersSection,
            "TrayIcon" => TrayIconSection,
            "Devices" => DevicesSection,
            "Hotkeys" => HotkeysSection,
            "Theme" => ThemeSection,
            "About" => AboutSection,
            _ => null,
        };
        if (activated == null) return;

        activated.Visibility = Visibility.Visible;
        InvokeRefreshOnShow(activated);
    }

    // Page-convention hook: invoke `RefreshOnShow()` via reflection if the page declares one.
    // Pages that need live re-population on every nav (HotkeysPage rows, GeneralPage install status)
    // declare the method; pages without it are skipped silently.
    private static void InvokeRefreshOnShow(System.Windows.Controls.UserControl page)
    {
        try
        {
            MethodInfo? method = page.GetType().GetMethod(
                RefreshOnShowMethodName,
                BindingFlags.Public | BindingFlags.Instance,
                Type.EmptyTypes);
            method?.Invoke(page, parameters: null);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"SettingsWindow.InvokeRefreshOnShow({page.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// In-flight confirm prompt. The overlay only supports one prompt at a time; a second call
    /// while a prompt is open auto-cancels the previous one so the new prompt's task is the only
    /// one a caller is awaiting.
    /// </summary>
    private TaskCompletionSource<bool>? _confirmDialogTcs;

    /// <summary>
    /// Show the confirm overlay with the supplied strings and resolve the returned task with the
    /// user's choice (true = confirm, false = cancel). Calls are expected to come in from the UI
    /// thread; only one prompt at a time is supported (matches the existing single-overlay UX).
    /// Replaces the deleted <c>IConfirmDialogService</c> interface; callers reach the host through
    /// <c>Window.GetWindow(this) is SettingsWindow</c> and call this method directly.
    /// </summary>
    public Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmText, string cancelText)
    {
        // Auto-cancel any earlier prompt that's still awaiting a click. The overlay UI is single-instance,
        // so a second open call would otherwise leave the first awaiter stuck forever.
        _confirmDialogTcs?.TrySetResult(false);

        ConfirmOverlayTitle.Text = title;
        ConfirmOverlayMessage.Text = message;
        ConfirmOverlayConfirmButton.Content = confirmText;
        ConfirmOverlayCancelButton.Content = cancelText;
        ConfirmOverlay.Visibility = Visibility.Visible;

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirmDialogTcs = tcs;
        return tcs.Task;
    }

    private void ConfirmOverlayConfirm_Click(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? tcs = _confirmDialogTcs;
        _confirmDialogTcs = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        tcs?.TrySetResult(true);
    }

    private void ConfirmOverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        TaskCompletionSource<bool>? tcs = _confirmDialogTcs;
        _confirmDialogTcs = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        tcs?.TrySetResult(false);
    }


    private void SaveAndNotify()
    {
        _settings.Save();
        _settings.RaiseChanged();
    }

}
