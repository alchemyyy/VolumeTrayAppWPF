using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Devices settings page. Hosts the comms-default tie toggle and the per-state visibility +
/// per-row button toggles for the device list. The volume-change ping toggles live on
/// <see cref="GeneralPage"/>; tray-menu device-link toggles live on <see cref="TrayIconPage"/>.
/// Every toggle is Tag-bound so
/// SettingsBindings.HandleBoolToggle does the dispatch; the cascading "even if disabled" child cards
/// drive their Visibility off XAML bindings against the parent toggles (InverseBoolToVisibility and
/// AndBoolToVisibility), so this code-behind only manages seed-from-settings + the bool dispatch.
/// </summary>
public partial class DevicesPage : UserControl
{
    private AppSettings? _settings;
    private bool _suppressChangeEvents;

    public DevicesPage() => InitializeComponent();

    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressChangeEvents = true;
        try
        {
            SetDefaultCommsToDefaultToggle.IsChecked = settings.SetDefaultCommsToDefault;

            ShowDisabledPlaybackToggle.IsChecked = settings.ShowDisabledPlaybackDevices;
            ShowDefaultPlaybackEvenIfDisabledToggle.IsChecked = settings.ShowDefaultPlaybackDeviceEvenIfDisabled;
            ShowDefaultCommsPlaybackEvenIfDisabledToggle.IsChecked = settings.ShowDefaultCommsPlaybackDeviceEvenIfDisabled;
            ShowDisconnectedPlaybackToggle.IsChecked = settings.ShowDisconnectedPlaybackDevices;

            ShowRecordingToggle.IsChecked = settings.ShowRecordingDevices;
            ShowDisabledRecordingToggle.IsChecked = settings.ShowDisabledRecordingDevices;
            ShowDefaultRecordingEvenIfDisabledToggle.IsChecked = settings.ShowDefaultRecordingDeviceEvenIfDisabled;
            ShowDefaultCommsRecordingEvenIfDisabledToggle.IsChecked = settings.ShowDefaultCommsRecordingDeviceEvenIfDisabled;

            ShowNotPresentToggle.IsChecked = settings.ShowNotPresentDevices;

            ShowPlaybackLockButtonToggle.IsChecked = settings.ShowLockButtonForPlayback;
            ShowPlaybackEqualizerAPOButtonToggle.IsChecked = settings.ShowEqualizerAPOButtonForPlayback;
            ShowPlaybackDefaultDeviceButtonToggle.IsChecked = settings.ShowDefaultDeviceButtonForPlayback;
            ShowPlaybackBatteryButtonToggle.IsChecked = settings.ShowBatteryButtonForPlayback;

            ShowRecordingLockButtonToggle.IsChecked = settings.ShowLockButtonForRecording;
            ShowRecordingEqualizerAPOButtonToggle.IsChecked = settings.ShowEqualizerAPOButtonForRecording;
            ShowRecordingListenButtonToggle.IsChecked = settings.ShowListenButtonForRecording;
            ShowRecordingDefaultDeviceButtonToggle.IsChecked = settings.ShowDefaultDeviceButtonForRecording;
            ShowRecordingBatteryButtonToggle.IsChecked = settings.ShowBatteryButtonForRecording;
        }
        finally
        {
            _suppressChangeEvents = false;
        }
    }

    private void BoolToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleBoolToggle(sender, _settings, SaveAndNotify, () => _suppressChangeEvents);
    }

    private void SaveAndNotify() => SettingsBindings.SaveAndNotify(_settings);
}
