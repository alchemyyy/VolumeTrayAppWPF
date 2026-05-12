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

    public const string SETTINGS = "";  // Setting (gear)
    public const string POWER = "";  // Power
    public const string INFO = "";  // Info
    public const string EXIT = "";  // ChromeClose
    public const string SUN = "";  // Brightness (sun, for light theme)
    public const string MOON = "";  // QuietHours (crescent moon, for dark theme)
    public const string WARNING = "";  // Warning (used by hotkey-conflict status badge)

    // Window-chrome / spinner / combobox chevrons. Catalog source of truth for code-side use;
    // XAML still embeds the raw codepoint until a glyph markup extension is added.
    public const string CHEVRON_DOWN = "";  // ChevronDown (combo dropdown arrow, spinner down)
    public const string CHEVRON_UP = "";    // ChevronUp (spinner up)

    // Flyout dock / undock toggle. DOCK / UNDOCK are the Fluent Icons names; the semantic
    // aliases FLYOUT_UNDOCK_ACTION / FLYOUT_REDOCK_ACTION read at call sites as the action a
    // click performs, since the button glyph flips with the IsUndocked state.
    public const string DOCK = "";    // Dock
    public const string UNDOCK = "";  // Undock
    public const string FLYOUT_UNDOCK_ACTION = DOCK;    // shown docked - click undocks the flyout
    public const string FLYOUT_REDOCK_ACTION = UNDOCK;  // shown undocked - click redocks the flyout

    // ===========================================================================
    // Volume tier glyphs (speaker icons; tier selection lives in GetVolumeTier)
    // ===========================================================================

    public const string PLAYBACK_VOLUME_MUTE = "";  // Mute
    public const string PLAYBACK_VOLUME_SILENT = "";  // Volume0 (silent, no waves)
    public const string PLAYBACK_VOLUME_LOW = "";  // Volume1
    public const string PLAYBACK_VOLUME_MID = "";  // Volume2
    public const string PLAYBACK_VOLUME_HIGH = "";  // Volume3
    // Semantic alias for the titlebar sound-settings entrypoint glyph. Same codepoint as
    // PLAYBACK_VOLUME_HIGH (Volume3) - declared separately so the call site reads as intent
    // ("open Sound settings"), not as a volume tier glyph.
    public const string SOUND_SETTINGS = "";  // Volume3 (reused for Sound settings entry)

    public const string MICROPHONE = "";  // Mic
    public const string MICROPHONE_OFF = "";  // Mic Off 2
    //public const string MICROPHONE_OFF = "";  // Mic Off
    public const string MICROPHONE_SLEEP = "";  // Mic Sleep
    public const string MICROPHONE_ERROR = "";  // Mic Error
    public const string MICROPHONE_FILLED = "";  // Mic On
    public const string MICROPHONE_CLIPPING = "";  // Mic Clipping
    public const string MICROPHONE_LISTENING = "";  // Mic Listening
    public const string EAR_LISTEN = "";  // Ear (glyph for capture-device 'Listen to this device' toggle)

    // ===========================================================================
    // Device-row control button glyphs (exclusive mode, sound settings, equalizer APO)
    // ===========================================================================

    // Exclusive mode. The "allow applications to take exclusive control" checkbox state and
    // the "is an app currently holding exclusive control" state are projected onto the same
    // button - UNLOCK reads "free", LOCK reads "held".
    public const string LOCK = "";  // Lock
    public const string UNLOCK = "";  // Unlock

    // Open the device's Windows 11 modern Sound settings page.
    public const string SETTINGS_SOLID = "";  // Settings Solid

    // Equalizer APO availability. EQUALIZER is the main button glyph; SIGNAL_NOT_CONNECTED is
    // overlaid via the mini-glyph slot when the APO binary can't be found on the system.
    public const string EQUALIZER = "";  // Equalizer
    public const string SIGNAL_NOT_CONNECTED = "";  // Signal Not Connected



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

    public const string PLAYBACK_DEVICE_DEFAULT = "";  // Status Circle Inner (filled)
    public const string PLAYBACK_DEVICE_ENABLED = "";  // Status Circle Ring
    public const string PLAYBACK_DEVICE_DISABLED = "";  // Status Circle Error X
    public const string PLAYBACK_DEVICE_DEFAULT_COMMS = "";  // Phone (per spec for default comms device)

    // Per-app-icon overlays. APP_MUTE_OVERLAY is the small X stamped on a muted app's icon -
    // matches what the flyout actually renders today (VolumeFlyout.xaml uses E653, not E74F).
    // APP_FALLBACK is the stacked-network silhouette shown when AppIconResolver couldn't extract
    // a real icon for an app session.
    public const string APP_MUTE_OVERLAY = "";  // BlockedSite / mute X overlay
    public const string APP_FALLBACK = "";      // StackedNetwork (fallback when no real icon)

    // ===========================================================================
    // Window-chrome caption glyphs
    // ===========================================================================

    public const string CHROME_MINIMIZE = "";  // ChromeMinimize
    public const string CHROME_MAXIMIZE = "";  // ChromeMaximize
    public const string CHROME_RESTORE = "";  // ChromeRestore

    // ===========================================================================
    // Decorative shapes (slider-thumb default options)
    // ===========================================================================

    public const string CIRCLE = "";  // CircleFill
    public const string DIAMOND = "";  // DiamondSolid
    public const string STAR = "";  // FavoriteStarFill
    public const string SQUARE = "";  // CheckboxFill
    public const string HEART = "";  // HeartFill
}
