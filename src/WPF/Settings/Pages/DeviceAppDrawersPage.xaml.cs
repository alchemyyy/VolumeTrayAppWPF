using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Device App Drawers settings page. Owns the playback and recording drawer caps, the recording
/// display-type combo, and every icon-mode knob (centering, scale, stack direction, icons-per-row).
/// Lives in its own tab so the Flyout page stays focused on flyout-level toggles. The shell calls
/// <see cref="LoadFromSettings"/> after construction to inject AppSettings and seed control values.
/// Tag-based mutations route through <see cref="SettingsBindings"/>. Child-card visibility cascades
/// off the recording-drawer-display-type combo via XAML bindings (no runtime BindingOperations).
/// Two adjacent SettingsCards replace the previous single-card title swap for Sliders vs Icons +
/// per-row vs per-column - each card carries its own localized title and its own bound spinner.
/// </summary>
public partial class DeviceAppDrawersPage : UserControl
{
    private AppSettings? _settings;
    private bool _suppressChangeEvents;

    public DeviceAppDrawersPage() => InitializeComponent();

    public void LoadFromSettings(AppSettings settings)
    {
        _settings = settings;
        _suppressChangeEvents = true;
        try
        {
            SettingsBindings.SelectComboByTag(
                RecordingAppDrawerDisplayTypeCombo,
                settings.RecordingAppDrawerDisplayType.ToString());
            SettingsBindings.SelectComboByTag(
                AppDrawerStackDirectionCombo,
                settings.AppDrawerStackDirection.ToString());
            SettingsBindings.SelectComboByTag(
                AppDrawerIconsCenterModeCombo,
                settings.AppDrawerIconsCenterMode.ToString());
            SettingsBindings.SelectComboByTag(
                CaptureActivityIndicatorCombo,
                settings.CaptureActivityIndicator.ToString());

            SettingsBindings.BindSpinner(
                AppDrawerIconsCenterSoftMaxBox,
                () => settings.AppDrawerIconsCenterSoftMax,
                v => settings.AppDrawerIconsCenterSoftMax = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                AppDrawerIconScaleBox,
                () => settings.AppDrawerIconScalePercent,
                v => settings.AppDrawerIconScalePercent = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            // Per-row and per-column spinners are visually mutually exclusive (driven off the stack
            // direction combo in XAML) but read / write the same AppDrawerIconsPerRow setting -
            // the value is "icons per cross-axis row", and the displayed label is the only thing
            // the direction flips. Bind both so whichever card is showing reflects the live value.
            SettingsBindings.BindSpinner(
                AppDrawerIconsPerRowBox,
                () => settings.AppDrawerIconsPerRow,
                v => settings.AppDrawerIconsPerRow = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                AppDrawerIconsPerColumnBox,
                () => settings.AppDrawerIconsPerRow,
                v => settings.AppDrawerIconsPerRow = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            // Playback drawer is hard-wired to Sliders, so the max-apps spinner always reads
            // PlaybackAppDrawerSlidersMaxApps; no mode switch needed here.
            SettingsBindings.BindSpinner(
                PlaybackDrawerMaxAppsBox,
                () => settings.PlaybackAppDrawerSlidersMaxApps,
                v => settings.PlaybackAppDrawerSlidersMaxApps = v,
                () => _suppressChangeEvents,
                SaveAndNotify);

            // Recording drawer has separate per-mode caps now sitting on adjacent SettingsCards.
            // The XAML toggles their visibility off the combo's selected Tag; each spinner writes
            // its own slot so the previous read / write switch and re-seed-on-mode-flip is gone.
            SettingsBindings.BindSpinner(
                RecordingDrawerMaxAppsSlidersBox,
                () => settings.RecordingAppDrawerSlidersMaxApps,
                v => settings.RecordingAppDrawerSlidersMaxApps = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
            SettingsBindings.BindSpinner(
                RecordingDrawerMaxAppsIconsBox,
                () => settings.RecordingAppDrawerIconsMaxRows,
                v => settings.RecordingAppDrawerIconsMaxRows = v,
                () => _suppressChangeEvents,
                SaveAndNotify);
        }
        finally
        {
            _suppressChangeEvents = false;
        }
    }

    private void EnumCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null) return;
        SettingsBindings.HandleEnumCombo(sender, _settings, SaveAndNotify, () => _suppressChangeEvents, this);
    }

    private void SaveAndNotify() => SettingsBindings.SaveAndNotify(_settings);
}
