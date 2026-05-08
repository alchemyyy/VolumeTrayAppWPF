using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace VolumeTrayAppWPF.Visuals;

/// <summary>
/// Skeleton tray-icon renderer: draws a Segoe Fluent Icons glyph on a transparent canvas
/// at the taskbar's native DPI / icon size, in a theme-aware foreground color.
///
/// Optionally composites a dimmed backdrop glyph behind the foreground, so a partial-state
/// glyph (e.g. low volume) reads against the silhouette of its full-state sibling - the same
/// trick the OS shell uses for the Wi-Fi signal indicator.
///
/// This is the customization seam for downstream apps - swap <see cref="Glyph"/>,
/// set <see cref="BackdropGlyph"/>, override <see cref="CustomColor"/>, or replace
/// <see cref="RenderBitmap"/> with app-specific artwork.
/// </summary>
public sealed class TrayIconRenderer(AppTheme theme) : IDisposable
{
    // Backdrop opacity for the dimmed full-state layer behind a partial foreground glyph
    private const double BackdropOpacity = 0.55;

    private Icon? _currentIcon;
    private bool _isDirty = true;
    private bool _disposed;
    private bool _isLightTheme;
    private Color? _customColor;
    private string _glyph = GlyphCatalog.SETTINGS;
    private string? _backdropGlyph;

    // Lazy init to avoid static-constructor COM issues with trimming.
    private static Typeface? _segoeFluent;
    private static Typeface SegoeFluent => _segoeFluent ??=
        new Typeface(new FontFamily("Segoe Fluent Icons"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>
    /// Whether the taskbar is using light theme.
    /// Forces a redraw on the next <see cref="CreateIcon"/> when changed.
    /// </summary>
    public bool IsLightTheme
    {
        get => _isLightTheme;
        set
        {
            if (_isLightTheme == value) return;
            _isLightTheme = value;
            _isDirty = true;
        }
    }

    /// <summary>
    /// Optional override for the icon foreground color.
    /// When null, the renderer falls back to the theme-aware default foreground.
    /// </summary>
    public Color? CustomColor
    {
        get => _customColor;
        set
        {
            if (_customColor == value) return;
            _customColor = value;
            _isDirty = true;
        }
    }

    /// <summary>
    /// The Segoe Fluent Icons codepoint string the tray icon renders.
    /// Defaults to <see cref="GlyphCatalog.SETTINGS"/>.
    /// </summary>
    public string Glyph
    {
        get => _glyph;
        set
        {
            if (string.IsNullOrEmpty(value) || _glyph == value) return;
            _glyph = value;
            _isDirty = true;
        }
    }

    /// <summary>
    /// Optional dimmed glyph drawn behind <see cref="Glyph"/>.
    /// Used to keep the full-state silhouette visible when the foreground is a partial state
    /// (e.g. full-volume speaker behind a low-volume speaker). Set to null to disable.
    /// </summary>
    public string? BackdropGlyph
    {
        get => _backdropGlyph;
        set
        {
            string? normalized = string.IsNullOrEmpty(value) ? null : value;
            if (_backdropGlyph == normalized) return;
            _backdropGlyph = normalized;
            _isDirty = true;
        }
    }

    /// <summary>
    /// Invalidates the cached icon so the next CreateIcon call re-renders.
    /// </summary>
    public void InvalidateCache() => _isDirty = true;

    /// <summary>
    /// Renders and returns the current tray icon.
    /// Returns null when nothing has changed and the cached icon is still valid.
    /// </summary>
    public Icon? CreateIcon()
    {
        if (!_isDirty && _currentIcon != null) return null;

        _isDirty = false;

        uint dpi = IconRenderingHelper.GetTaskbarDpi();
        int iconSize = IconRenderingHelper.GetIconSizeForDpi(dpi);

        Color foregroundColor = _customColor ?? theme.Foreground.For(IsLightTheme);

        Icon icon = IconRenderingHelper.BitmapToIcon(
            RenderBitmap(iconSize, _glyph, foregroundColor, _backdropGlyph));

        Icon? oldIcon = _currentIcon;
        _currentIcon = icon;
        oldIcon?.Dispose();

        return icon;
    }

    /// <summary>
    /// Renders a single centered glyph at the given size in the given color.
    /// Exposed so tooling (e.g. <see cref="AppIconGenerator"/>) can produce the same artwork
    /// at any resolution without going through System.Drawing.Icon.
    /// </summary>
    public static RenderTargetBitmap RenderBitmap(int size, string glyph, Color foregroundColor)
        => RenderBitmap(size, glyph, foregroundColor, null);

    /// <summary>
    /// Layered render: optional dimmed backdrop glyph, then the foreground glyph on top.
    /// Both glyphs are centered using the same metrics as the single-glyph path,
    /// so the foreground sits perfectly aligned over its full-state sibling.
    /// </summary>
    public static RenderTargetBitmap RenderBitmap(
        int size, string glyph, Color foregroundColor, string? backdropGlyph)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (!string.IsNullOrEmpty(backdropGlyph))
            {
                Color backdropColor = Color.FromArgb(
                    (byte)(foregroundColor.A * BackdropOpacity),
                    foregroundColor.R, foregroundColor.G, foregroundColor.B);
                DrawGlyph(dc, backdropGlyph, SegoeFluent, size, size, backdropColor);
            }

            DrawGlyph(dc, glyph, SegoeFluent, size, size, foregroundColor);
        }

        RenderTargetBitmap rtb = new(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    /// <summary>
    /// Draws a centered glyph using the specified settings.
    /// </summary>
    private static void DrawGlyph(
        DrawingContext dc, string glyph, Typeface typeface, double fontSize, int canvasSize, Color color)
    {
        FormattedText formattedText = new(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            new SolidColorBrush(color),
            1.0);

        double x = (canvasSize - formattedText.Width) / 2;
        double y = (canvasSize - formattedText.Height) / 2;

        dc.DrawText(formattedText, new Point(x, y));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
