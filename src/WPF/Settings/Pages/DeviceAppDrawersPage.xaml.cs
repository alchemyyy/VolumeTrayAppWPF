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
/// Device App Drawers settings page. Owns the playback and recording drawer caps, the recording
/// display-type combo, and every icon-mode knob (centering, scale, stack direction, icons-per-row).
/// Lives in its own tab so the Flyout page stays focused on flyout-level toggles. The shell calls
/// <see cref="LoadFromSettings"/> after construction to inject AppSettings and seed control values.
/// Tag-based mutations route through <see cref="SettingsBindings"/>.
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
    /// Hides icon-grid sub-options (centering + scale + stack + per-row) when the recording drawer
    /// is in Sliders mode, since those settings only affect the Icons drawer. Also retitles the
    /// icons-per-axis card: in vertical stack-direction modes (LeftRight / RightLeft) the same
    /// numeric setting caps icons per column instead of per row. The recording max-apps card title
    /// suffixes the active drawer mode (Sliders vs Icons), matching the user-facing rule that the
    /// same spinner only shows the cap for the mode the recording drawer is currently set to.
    /// </summary>
    private void UpdateChildCardVisibility()
    {
        if (_settings == null) return;

        bool iconsActive = _settings.RecordingAppDrawerDisplayType == AppDrawerDisplayType.Icons;
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

        string recTitleKey = iconsActive
            ? "Settings_Flyout_AppDrawerMaxApps_Icons_Title"
            : "Settings_Flyout_AppDrawerMaxApps_Sliders_Title";
        string recDescKey = iconsActive
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
