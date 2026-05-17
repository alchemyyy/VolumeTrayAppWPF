using Color = System.Windows.Media.Color;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Pure color-space math + hex parse/format helpers.
/// Extracted from TAColorPicker so the conversions are unit-testable
/// and reusable from any future swatch / picker / icon-recolor code.
/// No WPF / dispatcher / IO dependencies - just math against System.Windows.Media.Color.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// Standard HSV -> RGB conversion. hue in degrees [0, 360), sat / val in [0, 1].
    /// Returns an opaque color; callers substitute the alpha they want to preserve.
    /// </summary>
    public static Color HSVToRGB(double hue, double sat, double val)
    {
        if (sat <= 0)
        {
            byte gray = (byte)Math.Round(Math.Clamp(val, 0, 1) * 255);
            return Color.FromArgb(0xFF, gray, gray, gray);
        }

        double h = ((hue % 360) + 360) % 360 / 60.0;
        int sector = (int)Math.Floor(h);
        double f = h - sector;
        double p = val * (1 - sat);
        double q = val * (1 - sat * f);
        double t = val * (1 - sat * (1 - f));

        (double r, double g, double b) = sector switch
        {
            0 => (val, t, p),
            1 => (q, val, p),
            2 => (p, val, t),
            3 => (p, q, val),
            4 => (t, p, val),
            _ => (val, p, q),
        };

        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }

    /// <summary>
    /// Standard RGB -> HSV conversion. Inputs in [0, 255]; outputs hue in [0, 360), sat / val in [0, 1].
    /// Hue is undefined when sat is 0 - callers must decide whether to keep a previously remembered hue.
    /// </summary>
    public static (double Hue, double Sat, double Val) RGBToHSV(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double val = max;
        double sat = max == 0 ? 0 : delta / max;
        double hue = 0;

        if (delta > 0)
        {
            if (max == rd) hue = 60.0 * (((gd - bd) / delta) % 6);
            else if (max == gd) hue = 60.0 * ((bd - rd) / delta + 2);
            else hue = 60.0 * ((rd - gd) / delta + 4);
        }

        if (hue < 0) hue += 360;
        return (hue, sat, val);
    }

    /// <summary>Formats an ARGB color as an 8-char hex string (AARRGGBB), no leading "#".</summary>
    public static string FormatARGB(Color color) =>
        $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Formats an ARGB color as an 8-char hex string in RGBA order (RRGGBBAA), no leading "#".</summary>
    public static string FormatRGBA(Color color) =>
        $"{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    /// <summary>
    /// Parses a hex string in either ARGB (AARRGGBB) or RGBA (RRGGBBAA) byte order.
    /// Accepts a leading '#' and is case-insensitive.
    /// 6-char input is treated as RGB with alpha defaulted to 0xFF (in either order, since alpha is absent).
    /// Returns false on any malformed input - the caller leaves the current color untouched.
    /// </summary>
    public static bool TryParseHex(string input, bool ARGBOrder, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        string h = input.Trim().TrimStart('#');
        if (h.Length != 6 && h.Length != 8) return false;

        try
        {
            if (h.Length == 6)
            {
                byte r = Convert.ToByte(h[..2], 16);
                byte g = Convert.ToByte(h[2..4], 16);
                byte b = Convert.ToByte(h[4..6], 16);
                color = Color.FromArgb(0xFF, r, g, b);
                return true;
            }

            byte b0 = Convert.ToByte(h[..2], 16);
            byte b1 = Convert.ToByte(h[2..4], 16);
            byte b2 = Convert.ToByte(h[4..6], 16);
            byte b3 = Convert.ToByte(h[6..8], 16);
            color = ARGBOrder
                ? Color.FromArgb(b0, b1, b2, b3)
                : Color.FromArgb(b3, b0, b1, b2);
            return true;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Rec. 709 perceptual luminance against [0, 255] byte channels.
    /// Returns the same units (0-255 scale) so callers compare against a 128 midpoint
    /// to flip a white-or-black overlay without further scaling.
    /// </summary>
    public static double PerceptualLuminance(Color rgb) =>
        0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B;

    /// <summary>
    /// Computes the visible color of a foreground rgb at the picker's alpha gradient thumb.
    /// The thumb sits on top of the alpha gradient (bottom = opaque rgb, top = transparent rgb),
    /// which itself sits over the window's themed background. The thumb's vertical position
    /// tracks its alpha (IsDirectionReversed: value 255 = bottom = opaque), so the gradient
    /// alpha at the thumb's pixels equals the thumb's own alpha. Both blends are linear:
    ///     visible_gradient = rgb*a + bg*(1-a)
    ///     visible_thumb    = rgb*a + visible_gradient*(1-a)
    ///                      = rgb*a*(2-a) + bg*(1-a)^2
    /// Reduces correctly at the extremes: a=1 -> rgb, a=0 -> bg.
    /// </summary>
    public static Color AlphaOverGradientOverBackground(Color rgb, Color background)
    {
        double a = rgb.A / 255.0;
        double rgbWeight = a * (2 - a);
        double bgWeight = (1 - a) * (1 - a);

        byte r = (byte)Math.Round(rgb.R * rgbWeight + background.R * bgWeight);
        byte g = (byte)Math.Round(rgb.G * rgbWeight + background.G * bgWeight);
        byte b = (byte)Math.Round(rgb.B * rgbWeight + background.B * bgWeight);
        return Color.FromRgb(r, g, b);
    }
}
