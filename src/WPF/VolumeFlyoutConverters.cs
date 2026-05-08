using System.Globalization;
using System.Windows.Data;
using VolumeTrayAppWPF.Visuals;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Maps the 0.0 - 1.0 scalar volume that COM exposes onto the 0 - 100 percent surface
/// the slider and label show. Used both ways: as a Slider.Value TwoWay binding
/// (percent slot -> scalar field) and as a one-way label converter
/// (rounds to whole percent for display).
/// </summary>
internal sealed class ScalarToPercentConverter : IValueConverter
{
    public static readonly ScalarToPercentConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is float f) return Math.Round(f * 100.0);
        if (value is double d) return Math.Round(d * 100.0);
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Slider.Value comes through as double; clamp before division so a runaway DP value
        // can't push the underlying ISimpleAudioVolume call out of [0,1] and trip an HRESULT.
        double v = System.Convert.ToDouble(value, culture) / 100.0;
        if (v < 0) v = 0;
        else if (v > 1) v = 1;
        return (float)v;
    }
}

/// <summary>
/// Selects the speaker glyph shown next to the device-row slider, using the
/// <see cref="GlyphCatalog"/> volume tier constants so the tray icon and the
/// device-row icon stay visually in sync.
/// Inputs: [0]=Volume(scalar), [1]=IsMuted(bool).
/// </summary>
internal sealed class VolumeGlyphConverter : IMultiValueConverter
{
    public static readonly VolumeGlyphConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return GlyphCatalog.VOLUME_SILENT;

        bool muted = values[1] is bool b && b;
        if (muted) return GlyphCatalog.VOLUME_MUTE;

        double scalar = values[0] switch
        {
            float f => f,
            double d => d,
            _ => 0.0,
        };

        // Bands chosen so a slight nudge off zero already swaps to "low" - matches Win11 system tray behavior.
        if (scalar <= 0.001) return GlyphCatalog.VOLUME_SILENT;
        if (scalar < 0.34)   return GlyphCatalog.VOLUME_LOW;
        if (scalar < 0.67)   return GlyphCatalog.VOLUME_MID;
        return GlyphCatalog.VOLUME_HIGH;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}