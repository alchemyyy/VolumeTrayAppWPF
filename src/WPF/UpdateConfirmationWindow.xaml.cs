using System.Windows;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Services;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Modal "an update is available" dialog. Shows the release name in the title, a brief sentence
/// describing the action, and the full release body in a scrollable area whose visible height is
/// clamped to roughly 16 lines. Result is surfaced via <see cref="Window.DialogResult"/> -
/// true when the user accepts, false on Cancel / close / Escape.
/// </summary>
public partial class UpdateConfirmationWindow : Window
{
    // Lines visible before the changelog scrolls; the body is clamped to this height so a release
    // with a very long body doesn't push the dialog off the screen.
    private const int MaxVisibleChangelogLines = 16;
    // Pixels per line. Matches the LineHeight on the changelog TextBlock so the
    // visible window contains the right number of full lines.
    private const double ChangelogLineHeightPx = 16;

    public UpdateConfirmationWindow(UpdateInfo info)
    {
        InitializeComponent();

        string titleFormat = LocalizationManager.Instance["UpdateDialog_TitleFormat"];
        string title = string.Format(titleFormat, info.ReleaseName);
        Title = title;
        TitleText.Text = title;
        HeaderText.Text = title;

        ChangelogText.Text = string.IsNullOrWhiteSpace(info.Changelog)
            ? LocalizationManager.Instance["UpdateDialog_NoChangelog"]
            : info.Changelog;

        // Plus a couple of px padding for the line-stacking strategy's overshoot. Anything taller
        // is hidden behind the ScrollViewer; users scroll to see it. 16 lines covers all but the
        // chattiest releases.
        ChangelogScrollViewer.MaxHeight = MaxVisibleChangelogLines * ChangelogLineHeightPx + 4;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
