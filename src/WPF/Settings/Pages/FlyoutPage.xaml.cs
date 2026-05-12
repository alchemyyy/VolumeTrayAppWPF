using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Flyout settings page. Owns the volume-flyout undock toggles plus the device-list layout, sort,
/// and recording-visibility controls. Drawer-shaped settings (playback / recording caps + icon-mode
/// knobs) live on <see cref="DeviceAppDrawersPage"/>. The shell calls <see cref="LoadFromSettings"/>
/// after construction to inject AppSettings and seed control values. Tag-based mutations route
/// through <see cref="SettingsBindings"/>.
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

            UpdateChildCardVisibility();
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
        UpdateChildCardVisibility();
    }

    private void EnumCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleEnumCombo(sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this);
        UpdateChildCardVisibility();
    }

    /// <summary>
    /// Hides the Intermix toggle when ShowRecordingDevicesInFlyout is off so the off-state UI
    /// doesn't dangle a dead child knob.
    /// </summary>
    private void UpdateChildCardVisibility()
    {
        if (_settings == null) return;
        IntermixRecordingCard.Visibility = _settings.ShowRecordingDevicesInFlyout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
