using System.Xml.Serialization;
using Color = System.Windows.Media.Color;

namespace VolumeTrayAppWPF.Models;

/// <summary>
/// A user-overridable theme color with independent light and dark variants.
/// Either side may be null, meaning "unset" - the upstream resolver falls back to the per-color default.
/// While a color picker is open, TemporaryLightColor / TemporaryDarkColor short-circuit
/// the persisted hex values so the rest of the app sees the in-flight edit through the same Resolve path
/// without mutating (and risking persistence of) the saved hex until the user accepts.
/// Callers wire one or more change handlers via the (Action) ctor or Subscribe;
/// every mutation of LightHex / DarkHex / Temporary* fires the multicast handler.
/// </summary>
public class NullableThemeColor
{
    private string? _lightHex;
    private string? _darkHex;
    private Color? _tempLight;
    private Color? _tempDark;
    private Action? _changed;

    // Required for XmlSerializer.
    // Production callers should prefer the (Action) overload, or attach via Subscribe.
    public NullableThemeColor() { }

    // onChanged is invoked on every actual change (LightHex / DarkHex / Temporary*).
    public NullableThemeColor(Action onChanged) => Subscribe(onChanged);

    public void Subscribe(Action onChanged) => _changed += onChanged;

    public void Unsubscribe(Action onChanged) => _changed -= onChanged;

    [XmlElement]
    public string? LightHex
    {
        get => _lightHex;
        set
        {
            if (_lightHex == value) return;
            _lightHex = value;
            _changed?.Invoke();
        }
    }

    [XmlElement]
    public string? DarkHex
    {
        get => _darkHex;
        set
        {
            if (_darkHex == value) return;
            _darkHex = value;
            _changed?.Invoke();
        }
    }

    // Live-preview override for the light variant, set by the color picker on every edit
    // and cleared when the picker accepts (committed to LightHex) or aborts.
    // Never serialized.
    [XmlIgnore]
    public Color? TemporaryLightColor
    {
        get => _tempLight;
        set
        {
            if (_tempLight == value) return;
            _tempLight = value;
            _changed?.Invoke();
        }
    }

    // Live-preview override for the dark variant. Same lifecycle as TemporaryLightColor.
    [XmlIgnore]
    public Color? TemporaryDarkColor
    {
        get => _tempDark;
        set
        {
            if (_tempDark == value) return;
            _tempDark = value;
            _changed?.Invoke();
        }
    }

    public bool IsUnset => string.IsNullOrEmpty(LightHex) && string.IsNullOrEmpty(DarkHex);

    public Color? LightColor => TemporaryLightColor ?? TryParse(LightHex);
    public Color? DarkColor => TemporaryDarkColor ?? TryParse(DarkHex);

    private static Color? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        try
        {
            string hexString = hex.TrimStart('#');
            return hexString.Length switch
            {
                6 => Color.FromRgb(
                    Convert.ToByte(hexString[..2], 16),
                    Convert.ToByte(hexString[2..4], 16),
                    Convert.ToByte(hexString[4..6], 16)),
                8 => Color.FromArgb(
                    Convert.ToByte(hexString[..2], 16),
                    Convert.ToByte(hexString[2..4], 16),
                    Convert.ToByte(hexString[4..6], 16),
                    Convert.ToByte(hexString[6..8], 16)),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    public static string ToHex(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Public sibling of TryParse that yields <paramref name="fallback"/> when the input is null,
    /// empty, or malformed. Lets non-NullableThemeColor settings (e.g. AppSettings.MeterPeakColorHex)
    /// reuse the same hex parsing rules without duplicating the switch.
    /// </summary>
    public static Color ParseHexOrDefault(string? hex, Color fallback) => TryParse(hex) ?? fallback;

    // Resolves the override for the given theme.
    // Returns null when this side is unset so the upstream resolver falls through to the per-color default.
    // The unset side is never derived from the counterpart - editing only the light variant must not
    // rewrite what the dark variant displays (and vice versa).
    public Color? Resolve(bool isLightTheme) => isLightTheme ? LightColor : DarkColor;
}
