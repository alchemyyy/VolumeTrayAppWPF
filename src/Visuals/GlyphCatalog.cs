namespace VolumeTrayAppWPF.Visuals;

/// <summary>
/// Canonical Segoe Fluent Icons codepoint strings used across the app.
/// Single source of truth referenced by tray-icon renderers and the XML-overridable
/// <see cref="AppTheme"/> glyph property defaults.
/// Add new entries here rather than scattering raw codepoints through the codebase.
/// </summary>
internal static class GlyphCatalog
{
    // ===========================================================================
    // Generic UI glyphs (defaults for the AppTheme.Glyph* properties; user-overridable via theme.xml)
    // ===========================================================================

    public const string SETTINGS        = "\uE713";  // Setting (gear)
    public const string POWER           = "\uE7E8";  // Power
    public const string INFO            = "\uE946";  // Info
    public const string EXIT            = "\uE8BB";  // ChromeClose
    public const string SUN             = "\uE706";  // Brightness (sun, for light theme)
    public const string MOON            = "\uE708";  // QuietHours (crescent moon, for dark theme)
    public const string WARNING         = "\uE7BA";  // Warning (used by hotkey-conflict status badge)

    // ===========================================================================
    // Volume tier glyphs (speaker icons matching the bands in VolumeGlyphConverter)
    // ===========================================================================

    public const string VOLUME_MUTE     = "\uE74F";  // Mute
    public const string VOLUME_SILENT   = "\uE992";  // Volume0 (silent, no waves)
    public const string VOLUME_LOW      = "\uE993";  // Volume1
    public const string VOLUME_MID      = "\uE994";  // Volume2
    public const string VOLUME_HIGH     = "\uE995";  // Volume3

    // ===========================================================================
    // Window-chrome caption glyphs
    // ===========================================================================

    public const string CHROME_MAXIMIZE = "\uE922";  // ChromeMaximize
    public const string CHROME_RESTORE  = "\uE923";  // ChromeRestore

    // ===========================================================================
    // Decorative shapes (slider-thumb default options)
    // ===========================================================================

    public const string CIRCLE          = "\uE91F";  // CircleFill
    public const string DIAMOND         = "\uEA3B";  // DiamondSolid
    public const string STAR            = "\uE734";  // FavoriteStarFill
    public const string SQUARE          = "\uE73B";  // CheckboxFill
    public const string HEART           = "\uEB51";  // HeartFill
}
