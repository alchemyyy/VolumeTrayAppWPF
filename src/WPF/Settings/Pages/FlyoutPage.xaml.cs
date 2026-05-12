using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;
using BindingOperations = System.Windows.Data.BindingOperations;
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
            SettingsBindings.SelectComboByTag(
                RecordingAppDrawerDisplayTypeCombo,
                settings.RecordingAppDrawerDisplayType.ToString());
            SettingsBindings.SelectComboByTag(
                AppDrawerStackDirectionCombo,
                settings.AppDrawerStackDirection.ToString());

            ShowRecordingDevicesInFlyoutToggle.IsChecked = settings.ShowRecordingDevicesInFlyout;
            IntermixRecordingWithPlaybackInFlyoutToggle.IsChecked = settings.IntermixRecordingWithPlaybackInFlyout;
            ShowListenButtonInFlyoutToggle.IsChecked = settings.ShowListenButtonInFlyout;

            SettingsBindings.SelectComboByTag(
                AppDrawerIconsCenterModeCombo,
                settings.AppDrawerIconsCenterMode.ToString());
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
            SettingsBindings.BindSpinner(
                AppDrawerIconsPerRowBox,
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

            // Recording drawer carries two distinct caps (sliders vs icons). The spinner routes
            // its read / write to the cap that matches the current RecordingAppDrawerDisplayType;
            // EnumCombo_Changed reseeds the displayed value when the mode flips.
            SettingsBindings.BindSpinner(
                RecordingDrawerMaxAppsBox,
                () => ReadRecordingMaxAppsCurrent(settings),
                v => WriteRecordingMaxAppsCurrent(settings, v),
                () => _suppressChangeEvents,
                SaveAndNotify);

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
        // Flipping the recording drawer mode swaps which max-apps setting the spinner is bound to;
        // resync its displayed value to the new mode's cap. Suppress so the resync doesn't write back.
        _suppressChangeEvents = true;
        try
        {
            RecordingDrawerMaxAppsBox.Value = ReadRecordingMaxAppsCurrent(_settings);
        }
        finally
        {
            _suppressChangeEvents = false;
        }
        UpdateChildCardVisibility();
    }

    private static int ReadRecordingMaxAppsCurrent(AppSettings settings) =>
        settings.RecordingAppDrawerDisplayType == AppDrawerDisplayType.Icons
            ? settings.RecordingAppDrawerIconsMaxRows
            : settings.RecordingAppDrawerSlidersMaxApps;

    private static void WriteRecordingMaxAppsCurrent(AppSettings settings, int value)
    {
        if (settings.RecordingAppDrawerDisplayType == AppDrawerDisplayType.Icons)
            settings.RecordingAppDrawerIconsMaxRows = value;
        else
            settings.RecordingAppDrawerSlidersMaxApps = value;
    }

    /// <summary>
    /// Hides cascading toggles whose parent setting is off:
    ///   - Intermix toggle when ShowRecordingDevicesInFlyout is off.
    ///   - Icon-grid sub-options (centering + scale) when RecordingAppDrawerDisplayType is Sliders,
    ///     since those settings only affect the Icons drawer.
    /// Also retitles the icons-per-axis card: in vertical stack-direction modes (LeftRight /
    /// RightLeft) the same numeric setting caps icons per column instead of per row.
    /// </summary>
    private void UpdateChildCardVisibility()
    {
        if (_settings == null) return;
        IntermixRecordingCard.Visibility = _settings.ShowRecordingDevicesInFlyout ? Visibility.Visible : Visibility.Collapsed;

        bool iconsActive = _settings.RecordingAppDrawerDisplayType == Models.AppDrawerDisplayType.Icons;
        Visibility iconChildVisibility = iconsActive ? Visibility.Visible : Visibility.Collapsed;
        AppDrawerIconsCenteredCard.Visibility = iconChildVisibility;
        AppDrawerIconScaleCard.Visibility = iconChildVisibility;
        AppDrawerIconsPerRowCard.Visibility = iconChildVisibility;
        AppDrawerStackDirectionCard.Visibility = iconChildVisibility;
        // Soft-max width spinner is only meaningful in CenteredSoftMax; hide it under the other modes
        // (and outside the icons drawer entirely) so the card list doesn't show a dead knob.
        bool softMaxActive = iconsActive
            && _settings.AppDrawerIconsCenterMode == AppDrawerIconsCenterMode.CenteredSoftMax;
        AppDrawerIconsCenterSoftMaxCard.Visibility = softMaxActive ? Visibility.Visible : Visibility.Collapsed;

        bool perColumn = _settings.AppDrawerStackDirection is AppDrawerStackDirection.LeftRight
            or AppDrawerStackDirection.RightLeft;
        string titleKey = perColumn
            ? "Settings_Flyout_AppDrawerIconsPerColumn_Title"
            : "Settings_Flyout_AppDrawerIconsPerRow_Title";
        string descKey = perColumn
            ? "Settings_Flyout_AppDrawerIconsPerColumn_Description"
            : "Settings_Flyout_AppDrawerIconsPerRow_Description";
        // Bind through LocalizationManager so a live culture switch still refreshes the swapped labels.
        BindingOperations.SetBinding(AppDrawerIconsPerRowCard, SettingsCard.TitleProperty,
            new Binding($"[{titleKey}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(AppDrawerIconsPerRowCard, SettingsCard.DescriptionProperty,
            new Binding($"[{descKey}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay });

        // Recording drawer's max-apps card title suffixes the active drawer mode (Sliders vs Icons),
        // matching the user-facing rule that the same spinner only shows the cap for the mode the
        // recording drawer is currently set to.
        bool iconsMode = _settings.RecordingAppDrawerDisplayType == AppDrawerDisplayType.Icons;
        string recTitleKey = iconsMode
            ? "Settings_Flyout_AppDrawerMaxApps_Icons_Title"
            : "Settings_Flyout_AppDrawerMaxApps_Sliders_Title";
        string recDescKey = iconsMode
            ? "Settings_Flyout_AppDrawerMaxApps_Icons_Description"
            : "Settings_Flyout_AppDrawerMaxApps_Sliders_Description";
        BindingOperations.SetBinding(RecordingDrawerMaxAppsCard, SettingsCard.TitleProperty,
            new Binding($"[{recTitleKey}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay });
        BindingOperations.SetBinding(RecordingDrawerMaxAppsCard, SettingsCard.DescriptionProperty,
            new Binding($"[{recDescKey}]") { Source = LocalizationManager.Instance, Mode = BindingMode.OneWay });
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
