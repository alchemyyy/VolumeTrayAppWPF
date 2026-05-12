using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Flyout settings page. Owns the volume-flyout undock toggles plus the device-list layout, sort,
/// and recording-visibility controls. Drawer-shaped settings (playback / recording caps + icon-mode
/// knobs) live on <see cref="DeviceAppDrawersPage"/>. The shell calls <see cref="LoadFromSettings"/>
/// after construction to inject AppSettings and seed control values. Tag-based mutations route
/// through <see cref="SettingsBindings"/>. Child-card visibility is XAML-bound via BoolToVisibility
/// so no imperative refresh is needed after toggle changes.
/// </summary>
public partial class FlyoutPage : UserControl
{
    private AppSettings? _settings;
    private bool _suppressChangeEvents;

    public FlyoutPage() => InitializeComponent();

    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressChangeEvents = true;
        try
        {
            RestoreFlyoutUndockedOnStartupToggle.IsChecked = settings.RestoreFlyoutUndockedOnStartup;
            AllowFlyoutUndockToggle.IsChecked = settings.AllowFlyoutUndock;
            FlyoutHeaderAtBottomToggle.IsChecked = settings.FlyoutHeaderAtBottom;

            SettingsBindings.SelectComboByTag(SoundSettingsTargetCombo, settings.SoundSettingsTarget.ToString());

            SettingsBindings.SelectComboByTag(FlyoutDeviceLayoutCombo, settings.FlyoutDeviceLayout.ToString());
            SettingsBindings.SelectComboByTag(
                FlyoutDeviceTitlePositionCombo,
                settings.FlyoutDeviceTitlePosition.ToString());
            SettingsBindings.SelectComboByTag(FlyoutDeviceSortCombo, settings.FlyoutDeviceSort.ToString());

            ShowRecordingDevicesInFlyoutToggle.IsChecked = settings.ShowRecordingDevicesInFlyout;
            IntermixRecordingWithPlaybackInFlyoutToggle.IsChecked = settings.IntermixRecordingWithPlaybackInFlyout;
            ShowListenButtonInFlyoutToggle.IsChecked = settings.ShowListenButtonInFlyout;
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

    private void EnumCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleEnumCombo(sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this);
    }

    private void SaveAndNotify() => SettingsBindings.SaveAndNotify(_settings);
}
