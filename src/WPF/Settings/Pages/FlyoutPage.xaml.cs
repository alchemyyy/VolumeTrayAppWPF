using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Flyout settings page. Owns the volume-flyout undock toggles, the device-list layout / sort /
/// recording-visibility controls, and the peak-meter rendering knobs (unified toggle, bias, FPS,
/// sample rate, change ceiling). Drawer-shaped settings (playback / recording caps + icon-mode
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
            SettingsBindings.SelectComboByTag(
                FlyoutCommunicationsButtonVisibilityCombo,
                settings.FlyoutCommunicationsButtonVisibility.ToString());

            ShowRecordingDevicesInFlyoutToggle.IsChecked = settings.ShowRecordingDevicesInFlyout;
            IntermixRecordingWithPlaybackInFlyoutToggle.IsChecked = settings.IntermixRecordingWithPlaybackInFlyout;
            ShowDeviceFormatTextToggle.IsChecked = settings.ShowDeviceFormatText;
            ShowDeviceCodecTextToggle.IsChecked = settings.ShowDeviceCodecText;

            UnifiedPeakMeterToggle.IsChecked = settings.UnifiedPeakMeter;

            SettingsBindings.BindSpinner(
                UnifiedMeterBiasBox,
                () => settings.UnifiedMeterLowChannelBiasMultiplier,
                v => settings.UnifiedMeterLowChannelBiasMultiplier = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            SettingsBindings.BindSpinner(
                MeterPeakFpsBox,
                () => settings.MeterPeakFps,
                v => settings.MeterPeakFps = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            SettingsBindings.BindSpinner(
                MeterPeakSampleRateBox,
                () => settings.MeterPeakSampleRate,
                v => settings.MeterPeakSampleRate = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            SettingsBindings.BindSpinner(
                MeterPeakChangeCeilingBox,
                () => settings.MeterPeakChangeCeiling,
                v => settings.MeterPeakChangeCeiling = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
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
