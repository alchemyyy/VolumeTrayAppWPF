using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Maps <c>true</c> -> <see cref="Visibility.Collapsed"/> and <c>false</c> -> <see cref="Visibility.Visible"/>.
/// Mirror of the built-in <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/> for the
/// "show me this card while the parent toggle is OFF" pattern used by the cascading "even if disabled"
/// settings cards on DevicesPage.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}
