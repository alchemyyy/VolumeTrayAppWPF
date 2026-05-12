using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// IMultiValueConverter that maps an AND of any number of bool inputs to <see cref="Visibility"/>.
/// All-true -> Visible, any-false -> Collapsed.
/// Per-input inversion is supported via the optional <c>parameter</c>: a string of '0'/'1' flags
/// matching the binding order, where '1' inverts the corresponding bool before the AND.
/// e.g. parameter="01" combines (input[0]) AND (NOT input[1]).
/// Used by DevicesPage's recording-side "even if disabled" cards
/// which need (ShowRecording AND NOT ShowDisabledRecording).
/// </summary>
public sealed class AndBoolToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string? mask = parameter as string;
        for (int i = 0; i < values.Length; i++)
        {
            bool b = values[i] is bool bb && bb;
            bool invert = mask != null && i < mask.Length && mask[i] == '1';
            if (invert) b = !b;
            if (!b) return Visibility.Collapsed;
        }
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// AND of multiple <see cref="Visibility"/> inputs. All-Visible -> Visible, any-Collapsed -> Collapsed.
/// Used to compose multiple visibility predicates (each itself produced by a string-match converter)
/// without juggling intermediate bool conversions.
/// </summary>
public sealed class VisibilityAndConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (object v in values)
            if (v is Visibility vv && vv != Visibility.Visible) return Visibility.Collapsed;

        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
