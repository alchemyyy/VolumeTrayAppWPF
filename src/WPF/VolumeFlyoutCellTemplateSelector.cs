using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Models;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Picks the per-cell <see cref="DataTemplate"/> for the flyout's device list.
/// Two axes drive the selection:
///   - <see cref="AppSettings.FlyoutDeviceLayout"/> chooses whether apps stack above or below the device row.
///   - <see cref="AppSettings.RecordingAppDrawerDisplayType"/> chooses whether a capture cell's drawer
///     renders as full slider rows or as a compact icon grid; playback cells always use the slider variant.
/// The selector reads <see cref="AppServices.Settings"/> at selection time, and the host calls
/// <see cref="ItemsControl.Items.Refresh"/> when either setting flips so DataTemplateSelector's
/// per-ContentPresenter cache picks up the new mode.
/// </summary>
internal sealed class VolumeFlyoutCellTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AppsAboveTemplate { get; set; }
    public DataTemplate? AppsBelowTemplate { get; set; }
    public DataTemplate? AppsAboveGridTemplate { get; set; }
    public DataTemplate? AppsBelowGridTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        AppSettings? settings = AppServices.Settings;
        FlyoutDeviceLayoutStyle layout = settings?.FlyoutDeviceLayout ?? FlyoutDeviceLayoutStyle.AppsAboveDevice;
        AppDrawerDisplayType recordingDrawer = settings?.RecordingAppDrawerDisplayType ?? AppDrawerDisplayType.Icons;

        // Grid drawer applies only to capture cells; playback cells keep the slider drawer unchanged.
        bool useGrid = item is VolumeFlyoutCell cell
            && cell.IsCapture
            && recordingDrawer == AppDrawerDisplayType.Icons;

        if (useGrid)
        {
            return layout == FlyoutDeviceLayoutStyle.AppsBelowDevice ? AppsBelowGridTemplate : AppsAboveGridTemplate;
        }
        return layout == FlyoutDeviceLayoutStyle.AppsBelowDevice ? AppsBelowTemplate : AppsAboveTemplate;
    }
}
