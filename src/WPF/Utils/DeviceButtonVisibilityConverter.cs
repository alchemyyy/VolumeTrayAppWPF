using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Picks a device-row control button's Visibility from (IsCaptureDevice, showForPlayback, showForRecording).
/// Capture rows read the recording flag, render rows read the playback flag - one MultiBinding per button
/// replaces the two-MultiDataTrigger boilerplate the per-device-type visibility toggles would otherwise need.
/// </summary>
public sealed class DeviceButtonVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Visibility.Collapsed;

        bool isCapture = values[0] is bool b0 && b0;
        bool showForPlayback = values[1] is bool b1 && b1;
        bool showForRecording = values[2] is bool b2 && b2;

        bool show = isCapture ? showForRecording : showForPlayback;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
