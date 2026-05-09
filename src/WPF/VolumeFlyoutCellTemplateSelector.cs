using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Models;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Picks the per-cell <see cref="DataTemplate"/> for the flyout's device list based on the user's
/// <see cref="AppSettings.FlyoutDeviceLayout"/> choice. AppsAboveTemplate puts apps on top with the
/// device row in the footer band underneath; AppsBelowTemplate flips them. The selector reads
/// <see cref="AppServices.Settings"/> at selection time so a layout change can re-trigger selection
/// (the host calls <see cref="ItemsControl.Items.Refresh"/> when the setting flips).
/// </summary>
internal sealed class VolumeFlyoutCellTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AppsAboveTemplate { get; set; }
    public DataTemplate? AppsBelowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        FlyoutDeviceLayoutStyle layout = AppServices.Settings?.FlyoutDeviceLayout
            ?? FlyoutDeviceLayoutStyle.AppsAboveDevice;

        return layout == FlyoutDeviceLayoutStyle.AppsBelowDevice ? AppsBelowTemplate : AppsAboveTemplate;
    }
}
