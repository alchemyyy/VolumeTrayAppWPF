namespace VolumeTrayAppWPF.Visuals;

/// <summary>
/// Canonical Segoe Fluent Icons codepoint strings used across the app.
/// Single source of truth referenced by tray-icon renderers, XAML via {x:Static}, and the
/// XML-overridable <see cref="AppTheme"/> glyph property defaults.
/// Codepoints are written as \uXXXX escapes so the file stays ASCII per CLAUDE.md.
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

    // Window-chrome / spinner / combobox chevrons.
    public const string CHEVRON_UP = "\uE96D";    // ChevronUp (spinner up)
    public const string CHEVRON_DOWN = "\uE96E";  // ChevronDown (combo dropdown arrow, spinner down)

    public const string COMMUNICATIONS_ACTIVITY = "\uE77E"; // Incoming Call

    // Flyout dock / undock toggle. DOCK / UNDOCK are the Fluent Icons names; the semantic
    // aliases FLYOUT_UNDOCK_ACTION / FLYOUT_REDOCK_ACTION read at call sites as the action a
    // click performs, since the button glyph flips with the IsUndocked state.
    public const string DOCK = "\uE75B";    // Dock
    public const string UNDOCK = "\uE75A";  // Undock
    public const string FLYOUT_UNDOCK_ACTION = DOCK;    // shown docked - click undocks the flyout
    public const string FLYOUT_REDOCK_ACTION = UNDOCK;  // shown undocked - click redocks the flyout

    // ===========================================================================
    // Volume tier glyphs (speaker icons; tier selection lives in GetVolumeTier)
    // ===========================================================================

    public const string PLAYBACK_VOLUME_MUTE = "\uE74F";  // Mute
    public const string PLAYBACK_VOLUME_SILENT = "\uE992";  // Volume0 (silent, no waves)
    public const string PLAYBACK_VOLUME_LOW = "\uE993";  // Volume1
    public const string PLAYBACK_VOLUME_MID = "\uE994";  // Volume2
    public const string PLAYBACK_VOLUME_HIGH = "\uE995";  // Volume3
    // Semantic alias for the titlebar sound-settings entrypoint glyph. Same codepoint as
    // PLAYBACK_VOLUME_HIGH (Volume3) - declared separately so the call site reads as intent
    // ("open Sound settings"), not as a volume tier glyph.
    public const string SOUND_SETTINGS = "\uE995";  // Volume3 (reused for Sound settings entry)

    public const string MICROPHONE = "\uE720";  // Mic
    public const string MICROPHONE_OFF = "\uF781";  // Mic Off 2
    //public const string MICROPHONE_OFF = "\uEC54";  // Mic Off
    public const string MICROPHONE_SLEEP = "\uEC55";  // Mic Sleep
    public const string MICROPHONE_ERROR = "\uEC56";  // Mic Error
    public const string MICROPHONE_FILLED = "\uEC71";  // Mic On
    public const string MICROPHONE_CLIPPING = "\uEC72";  // Mic Clipping
    public const string MICROPHONE_LISTENING = "\uF12E";  // Mic Listening
    public const string EAR_LISTEN = "\uF270";  // Ear (glyph for capture-device 'Listen to this device' toggle)

    // ===========================================================================
    // Device-row control button glyphs (exclusive mode, sound settings, equalizer APO)
    // ===========================================================================

    // Exclusive mode. The "allow applications to take exclusive control" checkbox state and
    // the "is an app currently holding exclusive control" state are projected onto the same
    // button - UNLOCK reads "free", LOCK reads "held".
    public const string LOCK = "\uE72E";  // Lock
    public const string UNLOCK = "\uE785";  // Unlock

    // Open the device's Windows 11 modern Sound settings page.
    public const string SETTINGS_SOLID = "\uF8B0";  // Settings Solid

    // Equalizer APO availability. EQUALIZER is the main button glyph; SIGNAL_NOT_CONNECTED is
    // overlaid via the mini-glyph slot when the APO binary can't be found on the system.
    public const string EQUALIZER = "\uE9E9";  // Equalizer
    public const string SIGNAL_NOT_CONNECTED = "\uE871";  // Signal Not Connected



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
    public const string PLAYBACK_DEVICE_DEFAULT_COMMS = "\uE95B"; // Headset

    // Per-app-icon overlays. APP_MUTE_OVERLAY is the small X stamped on a muted app's icon -
    // matches what the flyout actually renders (BlockedSite, not Mute).
    // APP_FALLBACK is shown when AppIconResolver couldn't extract
    // a real icon for an app session.
    public const string APP_MUTE_OVERLAY = "\uE653";  // BlockedSite / mute X overlay
    public const string APP_FALLBACK = "\uE978";      // Presence Chicklet



    // ===========================================================================
    // Window-chrome caption glyphs
    // ===========================================================================

    public const string CHROME_MINIMIZE = "\uE921";  // ChromeMinimize
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
