using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Maps "binding value equals converter parameter" -> <see cref="Visibility.Visible"/>,
/// non-equal -> <see cref="Visibility.Collapsed"/>.
/// String compare is ordinal and case-sensitive; null and non-string values stringify before compare.
/// Used by the device-app-drawers page to drive two adjacent SettingsCards' visibility off a single
/// ComboBox's selected-item Tag (e.g. "Icons" vs "Sliders" for the recording drawer display type)
/// without juggling runtime BindingOperations.SetBinding to swap localized titles on one card.
/// </summary>
public sealed class StringEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? a = value?.ToString();
        string? b = parameter?.ToString();
        return string.Equals(a, b, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps "binding value matches any pipe-separated token in converter parameter" -> Visible.
/// e.g. ConverterParameter="LeftRight|RightLeft" returns Visible iff the bound string equals either token.
/// Inverse pattern: prefix the parameter with "!" to return Collapsed on match (Visible otherwise).
/// </summary>
public sealed class StringInSetVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string spec = parameter?.ToString() ?? string.Empty;
        bool invert = spec.StartsWith('!');
        if (invert) spec = spec[1..];

        string current = value?.ToString() ?? string.Empty;
        bool match = false;
        foreach (string token in spec.Split('|'))
            if (string.Equals(current, token, StringComparison.Ordinal)) { match = true; break; }

        if (invert) match = !match;
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
