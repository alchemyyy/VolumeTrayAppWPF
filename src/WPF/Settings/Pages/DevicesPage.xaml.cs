using System.Windows;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Settings.Utils;
using UserControl = System.Windows.Controls.UserControl;

namespace VolumeTrayAppWPF.WPF.Settings.Pages;

/// <summary>
/// Devices settings page. Hosts the defaulting / visibility / tray-menu toggles introduced for the
/// device-icon feature. Every toggle is Tag-bound so SettingsBindings.HandleBoolToggle does the
/// dispatch; this page only manages seed-from-settings + child-card visibility for the cascading
/// "even if disabled" sections.
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

            ShowTrayRecordingLinkToggle.IsChecked = settings.ShowTrayMenuRecordingLink;
            ShowTraySoundsLinkToggle.IsChecked = settings.ShowTrayMenuSoundsLink;
            ShowTrayCommunicationsLinkToggle.IsChecked = settings.ShowTrayMenuCommunicationsLink;
            ShowTrayDeviceLinksToggle.IsChecked = settings.ShowTrayMenuDeviceLinks;

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

    /// <summary>
    /// Hides the cascading child cards under their parent toggles per the precedence the user spec
    /// requires:
    ///   * "show default ... even if disabled" only matters while the broad "show disabled" gate is OFF
    ///   * the recording-side children all hide when the master ShowRecordingDevices is OFF
    /// Visibility, not IsEnabled - the off-state UI stays clean rather than parading dimmed toggles
    /// the user can't act on.
    /// </summary>
    private void UpdateChildCardVisibility()
    {
        if (_settings == null) return;

        bool playbackDisabledShown = _settings.ShowDisabledPlaybackDevices;
        ShowDefaultPlaybackEvenIfDisabledCard.Visibility = playbackDisabledShown ? Visibility.Collapsed : Visibility.Visible;
        ShowDefaultCommsPlaybackEvenIfDisabledCard.Visibility = playbackDisabledShown ? Visibility.Collapsed : Visibility.Visible;

        bool recordingShown = _settings.ShowRecordingDevices;
        Visibility recordingChildVis = recordingShown ? Visibility.Visible : Visibility.Collapsed;
        ShowDisabledRecordingCard.Visibility = recordingChildVis;

        // The "even if disabled" recording cards live under the recording master AND under the
        // recording-disabled gate - both must be in the right state for the cards to show.
        bool recordingDisabledShown = _settings.ShowDisabledRecordingDevices;
        Visibility evenIfDisabledVis = recordingShown && !recordingDisabledShown
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShowDefaultRecordingEvenIfDisabledCard.Visibility = evenIfDisabledVis;
        ShowDefaultCommsRecordingEvenIfDisabledCard.Visibility = evenIfDisabledVis;
    }

    private void SaveAndNotify()
    {
        if (_settings == null) return;
        _settings.Save();
        _settings.RaiseChanged();
    }
}
