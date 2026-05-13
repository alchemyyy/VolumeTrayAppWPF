using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// About page. Owns the build/runtime info rows, the Github hyperlink, and the auto-update section
/// (three setting toggles, a check-interval spinner, and a card containing the manual
/// "Check for updates" + "Install update" actions).
/// The shell calls <see cref="LoadFromSettings"/> after construction with the live AppSettings
/// reference and <see cref="RefreshOnShow"/> on every nav to this tab.
/// Subscribes to <see cref="UpdateCheckService.StateChanged"/> while loaded so the action card
/// and Install-button label flip live without depending on Settings.Changed.
/// </summary>
public partial class AboutPage : UserControl
{
    // Tick rate of the wall-clock timer that decides whether the "Install update" button should read
    // "Version stale". The transition is driven by a wall-clock comparison against LastCheckTimeUtc,
    // so a one-second tick is both more than fast enough and cheap (only runs while the page is loaded).
    private const int StaleCheckTimerIntervalMs = 1_000;

    private AppSettings? _settings;
    private UpdateCheckService? _updateService;
    private DispatcherTimer? _staleTimer;
    private bool _suppressChangeEvents;

    public AboutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void RefreshOnShow()
    {
        BuildNumberText.Text = BuildInfo.BuildNumber.ToString();
        RuntimeText.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        RefreshUpdateUi();
    }

    /// <summary>
    /// Wires the page's toggles and spinner to the supplied settings instance. Idempotent across
    /// repeat calls so a settings reload re-seeds the controls without leaving stale handlers attached.
    /// </summary>
    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressChangeEvents = true;
        try
        {
            CheckForUpdatesToggle.IsChecked = settings.CheckForUpdatesEnabled;
            ShowUpdateNotificationsToggle.IsChecked = settings.ShowUpdateNotificationsEnabled;
            ShowUpdateButtonToggle.IsChecked = settings.ShowUpdateButtonInFlyout;

            // Spinner is in minutes for the user; underlying setting is milliseconds. Clamp on load
            // so a hand-edited settings.xml that drifted out of range comes back into UI bounds without
            // throwing in NumericSpinner.Value (which would reject the out-of-range write).
            int minutes = settings.UpdateCheckIntervalMs / 60_000;
            if (minutes < 1) minutes = 1;
            if (minutes > 1440) minutes = 1440;
            UpdateIntervalMinutesBox.Value = minutes;
            UpdateIntervalMinutesBox.ValueChanged -= UpdateIntervalMinutesBox_ValueChanged;
            UpdateIntervalMinutesBox.ValueChanged += UpdateIntervalMinutesBox_ValueChanged;
        }
        finally
        {
            _suppressChangeEvents = false;
        }

        RefreshUpdateUi();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _updateService = AppServices.UpdateCheckService;
        if (_updateService != null) _updateService.StateChanged += OnUpdateStateChanged;

        // Wall-clock tick so the Install-update button can transition into "Version stale" without
        // any external event firing. Started here and torn down on Unload so we don't burn CPU when
        // the user is in another section of the Settings window.
        _staleTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(StaleCheckTimerIntervalMs),
        };
        _staleTimer.Tick += OnStaleTimerTick;
        _staleTimer.Start();

        RefreshUpdateUi();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_updateService != null)
        {
            _updateService.StateChanged -= OnUpdateStateChanged;
            _updateService = null;
        }

        if (_staleTimer != null)
        {
            _staleTimer.Stop();
            _staleTimer.Tick -= OnStaleTimerTick;
            _staleTimer = null;
        }
    }

    private void OnUpdateStateChanged() => Dispatcher.BeginInvoke(RefreshUpdateUi);

    private void OnStaleTimerTick(object? sender, EventArgs e) => RefreshUpdateUi();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void BoolToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleBoolToggle(sender, _settings, SaveAndNotify, () => _suppressChangeEvents);
        RefreshUpdateUi();
    }

    private void UpdateIntervalMinutesBox_ValueChanged(object? sender, int minutes)
    {
        if (_suppressChangeEvents || _settings == null) return;
        int ms = minutes * 60_000;
        if (_settings.UpdateCheckIntervalMs == ms) return;
        _settings.UpdateCheckIntervalMs = ms;
        SaveAndNotify();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckService? svc = _updateService ?? AppServices.UpdateCheckService;
        if (svc == null) return;

        CheckForUpdatesButton.IsEnabled = false;
        try
        {
            await svc.CheckNowAsync();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AboutPage.CheckForUpdatesButton_Click: {ex.Message}");
        }
        finally
        {
            CheckForUpdatesButton.IsEnabled = true;
            RefreshUpdateUi();
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckService? svc = _updateService ?? AppServices.UpdateCheckService;
        UpdateInfo? info = svc?.AvailableUpdate;
        if (svc == null || info == null) return;

        UpdateConfirmationWindow dialog = new(info) { Owner = Window.GetWindow(this) };
        bool? result = dialog.ShowDialog();
        if (result != true) return;

        InstallUpdateButton.IsEnabled = false;
        CheckForUpdatesButton.IsEnabled = false;
        bool ok = false;
        try
        {
            ok = await svc.DownloadAndStageAsync(info);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AboutPage.InstallUpdateButton_Click: {ex.Message}");
        }

        if (ok)
        {
            System.Windows.Application.Current?.Shutdown();
        }
        else
        {
            // Re-enable so the user can retry; RefreshUpdateUi handles the label based on current state.
            CheckForUpdatesButton.IsEnabled = true;
            RefreshUpdateUi();
        }
    }

    /// <summary>
    /// Single render loop for the action card. Three independent inputs - in-progress flag,
    /// available-update presence, and staleness of the last check timestamp - collapse onto two
    /// pieces of UI: the descriptive status text below the card title, and the Install button's
    /// label + IsEnabled state. Called whenever any of those inputs might have changed; the dispatcher
    /// is the only thread that touches the controls so no locking is needed.
    /// </summary>
    private void RefreshUpdateUi()
    {
        if (_settings == null) return;

        UpdateCheckService? svc = _updateService ?? AppServices.UpdateCheckService;
        if (svc == null)
        {
            UpdateStatusText.Text = LocalizationManager.Instance["Settings_About_UpdateStatus_Unavailable"];
            InstallUpdateButton.Content = LocalizationManager.Instance["Settings_About_InstallUpdate_UpToDate"];
            InstallUpdateButton.IsEnabled = false;
            return;
        }

        UpdateInfo? info = svc.AvailableUpdate;
        bool isChecking = svc.IsChecking;
        DateTime? last = svc.LastCheckTimeUtc;

        if (isChecking)
        {
            UpdateStatusText.Text = LocalizationManager.Instance["Settings_About_UpdateStatus_Checking"];
        }
        else if (info != null)
        {
            UpdateStatusText.Text = string.Format(
                LocalizationManager.Instance["Settings_About_UpdateStatus_AvailableFormat"], info.ReleaseName);
        }
        else if (last == null)
        {
            UpdateStatusText.Text = LocalizationManager.Instance["Settings_About_UpdateStatus_NeverChecked"];
        }
        else
        {
            UpdateStatusText.Text = string.Format(
                LocalizationManager.Instance["Settings_About_UpdateStatus_LastCheckedFormat"],
                FormatRelativeTimestamp(last.Value));
        }

        bool stale = ComputeStaleness(svc);
        if (info != null)
        {
            InstallUpdateButton.Content = LocalizationManager.Instance["Settings_About_InstallUpdate_Available"];
            InstallUpdateButton.IsEnabled = true;
        }
        else if (stale)
        {
            InstallUpdateButton.Content = LocalizationManager.Instance["Settings_About_InstallUpdate_Stale"];
            InstallUpdateButton.IsEnabled = false;
        }
        else
        {
            InstallUpdateButton.Content = LocalizationManager.Instance["Settings_About_InstallUpdate_UpToDate"];
            InstallUpdateButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Stale = the last successful check was at least (configured interval + grace) ago, or no check
    /// has ever happened since the service started. Never-checked is treated as not-stale here -
    /// the first poll runs within a few seconds of startup, so flagging it as stale would briefly
    /// alarm the user on every launch.
    /// </summary>
    private static bool ComputeStaleness(UpdateCheckService svc)
    {
        if (svc.LastCheckTimeUtc is not { } last) return false;

        AppSettings? settings = AppServices.Settings;
        int intervalMs = settings?.UpdateCheckIntervalMs ?? TimeConstants.UpdateCheckIntervalDefaultMs;
        TimeSpan threshold = TimeSpan.FromMilliseconds(intervalMs + TimeConstants.UpdateStaleGraceMs);
        return DateTime.UtcNow - last > threshold;
    }

    private static string FormatRelativeTimestamp(DateTime utc)
    {
        TimeSpan diff = DateTime.UtcNow - utc;
        if (diff < TimeSpan.FromSeconds(60))
            return LocalizationManager.Instance["Settings_About_RelativeTime_JustNow"];

        if (diff < TimeSpan.FromMinutes(60))
        {
            int minutes = Math.Max(1, (int)diff.TotalMinutes);
            return string.Format(LocalizationManager.Instance["Settings_About_RelativeTime_MinutesFormat"], minutes);
        }

        if (diff < TimeSpan.FromHours(24))
        {
            int hours = Math.Max(1, (int)diff.TotalHours);
            return string.Format(LocalizationManager.Instance["Settings_About_RelativeTime_HoursFormat"], hours);
        }

        int days = Math.Max(1, (int)diff.TotalDays);
        return string.Format(LocalizationManager.Instance["Settings_About_RelativeTime_DaysFormat"], days);
    }

    private void SaveAndNotify() => SettingsBindings.SaveAndNotify(_settings);
}
