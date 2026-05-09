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

    public const string SETTINGS = "\uE713";  // Setting (gear)
    public const string POWER = "\uE7E8";  // Power
    public const string INFO = "\uE946";  // Info
    public const string EXIT = "\uE8BB";  // ChromeClose
    public const string SUN = "\uE706";  // Brightness (sun, for light theme)
    public const string MOON = "\uE708";  // QuietHours (crescent moon, for dark theme)
    public const string WARNING = "\uE7BA";  // Warning (used by hotkey-conflict status badge)

    // ===========================================================================
    // Volume tier glyphs (speaker icons; tier selection lives in GetVolumeTier)
    // ===========================================================================

    public const string PLAYBACK_VOLUME_MUTE = "\uE74F";  // Mute
    public const string PLAYBACK_VOLUME_SILENT = "\uE992";  // Volume0 (silent, no waves)
    public const string PLAYBACK_VOLUME_LOW = "\uE993";  // Volume1
    public const string PLAYBACK_VOLUME_MID = "\uE994";  // Volume2
    public const string PLAYBACK_VOLUME_HIGH = "\uE995";  // Volume3

    public const string MICROPHONE = "\uE720";  // Mic
    public const string MICROPHONE_OFF = "\uF781";  // Mic Off 2
    //public const string MICROPHONE_OFF = "\uEC54";  // Mic Off
    public const string MICROPHONE_SLEEP = "\uEC55";  // Mic Sleep
    public const string MICROPHONE_ERROR = "\uEC56";  // Mic Error
    public const string MICROPHONE_FILLED = "\uEC71";  // Mic On
    public const string MICROPHONE_CLIPPING = "\uEC72";  // Mic Clipping
    public const string MICROPHONE_LISTENING = "\uF12E";  // Mic Listening



    // Single source of truth for volume-tier glyph selection. Shared by the tray-icon renderer
    // and the device-row VolumeGlyphConverter so the bands stay in lockstep. Bands chosen so a
    // slight nudge off zero already swaps to "low" - matches Win11 system tray behavior.
    public static string GetVolumeTier(float scalar, bool muted)
    {
        if (muted) return PLAYBACK_VOLUME_MUTE;
        return scalar switch
        {
            <= 0.001f => PLAYBACK_VOLUME_SILENT,
            < 0.34f => PLAYBACK_VOLUME_LOW,
            < 0.67f => PLAYBACK_VOLUME_MID,
            _ => PLAYBACK_VOLUME_HIGH
        };
    }

    // ===========================================================================
    // Device-icon button states (flyout footer + tray menu device entries)
    // ===========================================================================

    public const string PLAYBACK_DEVICE_DEFAULT = "\uF137";  // Status Circle Inner (filled)
    public const string PLAYBACK_DEVICE_ENABLED = "\uF138";  // Status Circle Ring
    public const string PLAYBACK_DEVICE_DISABLED = "\uF13D";  // Status Circle Error X
    public const string PLAYBACK_DEVICE_DEFAULT_COMMS = "\uE626";  // Phone (per spec for default comms device)
    public const string PLAYBACK_DEVICE_HOVER_MUTED = "\uE74F";  // Mute (hover preview = "click disables")
    public const string APP_MUTE_OVERLAY = "\uE74F";  // Block (50% overlay on app icons for mute)

    // ===========================================================================
    // Window-chrome caption glyphs
    // ===========================================================================

    public const string CHROME_MAXIMIZE = "\uE922";  // ChromeMaximize
    public const string CHROME_RESTORE = "\uE923";  // ChromeRestore

    // ===========================================================================
    // Decorative shapes (slider-thumb default options)
    // ===========================================================================

    public const string CIRCLE = "\uE91F";  // CircleFill
    public const string DIAMOND = "\uEA3B";  // DiamondSolid
    public const string STAR = "\uE734";  // FavoriteStarFill
    public const string SQUARE = "\uE73B";  // CheckboxFill
    public const string HEART = "\uEB51";  // HeartFill
}
