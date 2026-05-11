using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Flyout settings page. Owns the volume-flyout undock toggles plus the device-list layout, sort,
/// and recording-visibility controls. The shell calls <see cref="LoadFromSettings"/> after construction
/// to inject AppSettings and seed control values. Tag-based mutations route through
/// <see cref="SettingsBindings"/>.
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

            SettingsBindings.SelectComboByTag(FlyoutDeviceLayoutCombo, settings.FlyoutDeviceLayout.ToString());
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
    }

    /// <summary>
    /// Hides the intermix toggle when the master ShowRecordingDevicesInFlyout is off so the off-state
    /// UI stays uncluttered. Same precedence as the cascading toggles on DevicesPage.
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
