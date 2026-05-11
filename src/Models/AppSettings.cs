using System.IO;
using System.Xml;
using System.Xml.Serialization;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Visuals;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace VolumeTrayAppWPF.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum TrayIconStyle
{
    Dynamic,
    Static,
}

/// <summary>
/// Action taken when the tray icon is clicked or scrolled.
/// Skeleton ships with a no-op placeholder; extend with project-specific actions in your fork.
/// </summary>
public enum TrayClickAction
{
    Nothing,
    OpenSettings,
}

/// <summary>
/// Where the tray right-click menu appears.
/// Classic opens at the cursor position (the OS default for tray menus).
/// Modern docks the menu in the bottom-right corner of the primary work area with an 8px inset,
/// matching the Windows 11 system-flyout pattern.
/// </summary>
public enum ContextMenuPosition
{
    Classic,
    Modern,
}

public enum SliderThumbShape
{
    Glyph,
    Capsule,
}

/// <summary>
/// Which slice of an audio endpoint's name the tray context menu shows for each device row.
/// NameAndModel: full FriendlyName, e.g. "Speakers (Realtek(R) Audio)" - displayed as "Name+Model".
/// Name: PKEY_Device_DeviceDesc only, e.g. "Speakers".
/// Model: PKEY_DeviceInterface_FriendlyName only, e.g. "Realtek(R) Audio".
/// Playback and recording lists carry separate enum values so a user can keep playback verbose
/// while collapsing recording rows to just the model (or vice versa).
/// </summary>
public enum TrayMenuDeviceNameStyle
{
    NameAndModel,
    Name,
    Model,
}

/// <summary>
/// How each device's row is laid out relative to its per-app session sliders.
/// AppsAboveDevice: apps on top, device row underneath in the footer band - matches EarTrumpet.
/// AppsBelowDevice: device row on top, apps underneath. Bottom-up device list ordering applies in
/// either style; only the per-cell stacking flips.
/// </summary>
public enum FlyoutDeviceLayoutStyle
{
    AppsAboveDevice,
    AppsBelowDevice,
}

/// <summary>
/// Ordering rule for the device list in the flyout.
/// StateGrouped: default, default-comms, enabled, disabled, disconnected. Enumeration order breaks ties
/// inside each bucket. The list is rendered bottom-up so the default device sits closest to the user's
/// volume slider in the tray.
/// WindowsEnumeration: untouched MMDevice enumeration order, top-to-bottom matches Windows itself.
/// </summary>
public enum FlyoutDeviceSortOrder
{
    StateGrouped,
    WindowsEnumeration,
}

/// <summary>
/// Visual treatment that flags which apps in a recording device's drawer are currently capturing
/// from the microphone (their session State is Active).
/// DimInactive (default): icons of non-capturing apps are dimmed, matching how disabled devices are dimmed.
/// ActiveGlyph: a small overlay glyph is stamped on the icons of actively capturing apps; non-capturers untouched.
/// None: no visual indication.
/// </summary>
public enum CaptureActivityIndicator
{
    DimInactive,
    ActiveGlyph,
    None,
}

/// <summary>
/// A selectable volume-slider thumb glyph, stored with its own display properties
/// (font family, font size, width, height, horizontal scale) so that differently-proportioned glyphs
/// render correctly both in the dropdown preview and on the slider itself.
/// Defaults target Segoe Fluent Icons at 18px.
/// </summary>
public class SliderThumbGlyphOption
{
    [XmlAttribute] public string Name { get; set; } = "Circle";
    [XmlAttribute] public string Glyph { get; set; } = GlyphCatalog.CIRCLE;
    [XmlAttribute] public string FontFamily { get; set; } = "Segoe Fluent Icons";
    [XmlAttribute] public double FontSize { get; set; } = 18;
    [XmlAttribute] public double Width { get; set; } = 18;
    [XmlAttribute] public double Height { get; set; } = 18;

    // Horizontal layout-scale applied to the rendered glyph.
    // Lets a single glyph (e.g. Square) be repurposed as a narrower variant without authoring a new font character.
    [XmlAttribute] public double XScale { get; set; } = 1.0;

    // Glyph (default) draws a TextBlock from the Glyph string.
    // Capsule draws a rounded-rectangle Border using Width/Height with a fully rounded corner radius,
    // matching the OS toggle-switch pill aesthetic that can't be reproduced cleanly with a font character.
    [XmlAttribute] public SliderThumbShape Shape { get; set; } = SliderThumbShape.Glyph;

    [XmlIgnore] public bool IsGlyph => Shape == SliderThumbShape.Glyph;
    [XmlIgnore] public bool IsCapsule => Shape == SliderThumbShape.Capsule;

    public static List<SliderThumbGlyphOption> CreateDefaults() =>
    [
        new() { Name = "Capsule",  Shape = SliderThumbShape.Capsule, Width = 10, Height = 22 },
        new() { Name = "Circle",   Glyph = GlyphCatalog.CIRCLE,  FontSize = 18 },
        new() { Name = "Diamond",  Glyph = GlyphCatalog.DIAMOND, FontSize = 16 },
        new() { Name = "Star",     Glyph = GlyphCatalog.STAR,    FontSize = 18 },
        new() { Name = "Square",   Glyph = GlyphCatalog.SQUARE,  FontSize = 16 },
        new() { Name = "Heart",    Glyph = GlyphCatalog.HEART,   FontSize = 16 },
    ];
}

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

    // Required for XmlSerializer. Production callers should prefer the (Action) overload, or attach via Subscribe.
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
    // The unset side is never derived from the counterpart - editing only the light variant must not rewrite
    // what the dark variant displays (and vice versa).
    public Color? Resolve(bool isLightTheme) => isLightTheme ? LightColor : DarkColor;
}

/// <summary>
/// Root application settings class.
/// Skeleton scaffold with a few illustrative fields - extend with project-specific settings in your fork.
/// </summary>
[XmlRoot("AppSettings")]
public class AppSettings
{
    // General
    public bool RunOnStartup { get; set; } = true;
    public bool Autosave { get; set; } = true;
    // When the user promotes a device to default through this app (ctrl+click on the device icon
    // or via the tray menu), also promote it to the communications-role default. Strictly a
    // side-effect of our own write path; we never observe other apps' default-changes to mirror.
    public bool SetDefaultCommsToDefault { get; set; } = false;

    // Device visibility filters. The flyout / tray menu honor these when building lists.
    // Parent off -> children are forced off; child "even if disabled" toggles only matter when the
    // disabled-devices toggle is OFF (otherwise the disabled devices already show by default).
    public bool ShowDisabledPlaybackDevices { get; set; } = false;
    public bool ShowDefaultPlaybackDeviceEvenIfDisabled { get; set; } = true;
    public bool ShowDefaultCommsPlaybackDeviceEvenIfDisabled { get; set; } = true;
    public bool ShowDisconnectedPlaybackDevices { get; set; } = false;
    public bool ShowRecordingDevices { get; set; } = true;
    public bool ShowDisabledRecordingDevices { get; set; } = false;
    public bool ShowDefaultRecordingDeviceEvenIfDisabled { get; set; } = true;
    public bool ShowDefaultCommsRecordingDeviceEvenIfDisabled { get; set; } = true;

    // Persisted "last-seen active default" id per role / flow. AudioDeviceManager writes these
    // every time GetDefaultAudioEndpoint returns a real device, and reads them as a fallback
    // when the same lookup later comes back null - that null result, while a previously-default
    // device still exists in the device list, means the user disabled the active default and
    // Windows had no other active device of that role / flow to promote. The fallback restores
    // IsDefault on the disabled wrapper so the visibility filter under the
    // ShowDefault*EvenIfDisabled toggles has a target to act on.
    public string? LastKnownDefaultPlaybackDeviceId { get; set; }
    public string? LastKnownDefaultCommsPlaybackDeviceId { get; set; }
    public string? LastKnownDefaultRecordingDeviceId { get; set; }
    public string? LastKnownDefaultCommsRecordingDeviceId { get; set; }

    // Registry-only ghost endpoints surfaced via DeviceState.NotPresent: every USB DAC port the user
    // has ever plugged into, every previous GPU's HDMI outputs, every paired Bluetooth headset that
    // accumulated in the audio device registry. Off by default so the tray / flyout don't drown in
    // "Unknown Device" rows; opt-in for users who want to inspect or revive a ghost endpoint.
    // Cross-flow: applies to both render and capture NotPresent devices in one switch.
    public bool ShowNotPresentDevices { get; set; } = false;

    // Tray-menu quick links to the classic Sound control-panel tabs.
    public bool ShowTrayMenuRecordingLink { get; set; } = false;
    public bool ShowTrayMenuSoundsLink { get; set; } = false;
    public bool ShowTrayMenuCommunicationsLink { get; set; } = false;

    // Per-device link entries in the tray menu. When on, every visible enabled device gets a
    // sub-entry that opens the classic device-properties tab (same as ctrl+click on the device icon).
    public bool ShowTrayMenuDeviceLinks { get; set; } = false;
    // Apply a perceptual (exponential) curve when mapping the slider position to the system volume,
    // so equal slider deltas feel like equal loudness deltas. Off by default; raw linear mapping.
    public bool UseLogarithmicVolumeScale { get; set; } = false;
    // Audible feedback on device-slider changes only (not per-app sliders).
    // Plays the Windows default-beep system sound on mouse-up after a click/drag and on each wheel notch
    // over the device row, mirroring the OS volume slider's release feedback.
    public bool PlayDeviceVolumeChangeSound { get; set; } = true;
    // Same idea for per-app sliders. The wav plays through this app's audio session at MediaPlayer.Volume
    // scaled to the target app's slider value, so the feedback's loudness matches what the user just dialed
    // the app to. Caveat: it isn't injected into the target app's session - if the user has muted/lowered
    // VolumeTrayApp itself, the feedback gets attenuated again on top of that scalar.
    public bool PlayAppVolumeChangeSound { get; set; } = true;

    // Context menu
    public ContextMenuPosition ContextMenuPosition { get; set; } = ContextMenuPosition.Modern;
    public int ContextMenuFontSize { get; set; } = 15;

    // Per-flow device-name style for the tray context menu rows. Defaults to Everything so the
    // initial UX matches the prior behavior (full Windows FriendlyName).
    public TrayMenuDeviceNameStyle TrayMenuPlaybackDeviceNameStyle { get; set; } = TrayMenuDeviceNameStyle.NameAndModel;
    public TrayMenuDeviceNameStyle TrayMenuRecordingDeviceNameStyle { get; set; } = TrayMenuDeviceNameStyle.NameAndModel;

    // Cap on the rendered device-name length in the tray context menu. When the chosen name slice
    // exceeds this character count, the suffix is replaced with a 2-dot ellipsis ("..") to keep
    // the menu width predictable. Clamped to the spinner's [Min, Max] range so a corrupt
    // settings.xml can't push the value outside what the UI accepts.
    public const int TrayMenuDeviceNameMaxLengthDefault = 32;
    public const int TrayMenuDeviceNameLengthNumericBoxMin = 3;
    public const int TrayMenuDeviceNameLengthNumericBoxMax = 200;

    private int _trayMenuDeviceNameMaxLength = TrayMenuDeviceNameMaxLengthDefault;

    [XmlElement]
    public int TrayMenuDeviceNameMaxLength
    {
        get => _trayMenuDeviceNameMaxLength;
        set
        {
            int clamped = Math.Max(
                TrayMenuDeviceNameLengthNumericBoxMin,
                Math.Min(TrayMenuDeviceNameLengthNumericBoxMax, value));
            if (_trayMenuDeviceNameMaxLength == clamped) return;
            _trayMenuDeviceNameMaxLength = clamped;
        }
    }

    // Theme
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;
    public NullableThemeColor TextColor { get; set; } = new();
    public NullableThemeColor BackgroundColor { get; set; } = new();
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.Dynamic;
    public NullableThemeColor TrayIconColor { get; set; } = new();
    public bool EnableRoundedCorners { get; set; } = true;

    // Peak meter overlay drawn on top of the volume slider track.
    // Two-rate model mirroring EarTrumpet: SampleRate is how often we COM-read the raw peak from
    // IAudioMeterInformation (off the UI thread); Fps is how often the render timer advances the
    // step-counter lerp toward the most recent sample. Running Fps > SampleRate is what gives the
    // meter visible smoothness - dispatcher updates the lerp multiple times per sample interval,
    // and the screen at vsync catches a stepped sequence of intermediate values rather than a
    // snap-to-latest sequence.
    // Defaults: SampleRate=90, Fps=180 -> 2 interpolation steps per sample.
    // Both clamped 1..1000 so a corrupt settings.xml can't push either timer to insane rates.
    // ColorHex is a single solid color (no light/dark variant) - default opaque white.
    // TemporaryMeterPeakColor is the live-preview slot the color picker writes during a drag;
    // never serialized, mirrors NullableThemeColor.Temporary*.
    // Meter is two stacked overlays driven by the first two channels of IAudioMeterInformation:
    // MeterPeakColor paints the bar to min(L, R) (the level guaranteed in both channels), and
    // MeterPeakStereoColor paints on top to max(L, R). With a translucent stereo color the
    // mismatch between channels reads as a halo extending past the solid base bar; for mono
    // streams (or when L==R) the two bars coincide.
    public const string MeterPeakColorDefaultHex = "#FFFFFFFF";
    public const string MeterPeakStereoColorDefaultHex = "#80FFFFFF";
    public const int MeterPeakFpsDefault = 180;
    public const int MeterPeakSampleRateDefault = 90;

    // Per-redraw ceiling, in 0-100 volume units, on how far VolumeSlider's rendered peak can move
    // toward the incoming smoothed target. Caps single-frame jumps so a sudden silence-to-loud
    // (or loud-to-silence) transition ramps over a few frames instead of teleporting. 0 freezes
    // the meter; 100 disables the clamp (one-tick catch-up).
    public const int MeterPeakChangeCeilingDefault = 10;
    public const int MeterPeakChangeCeilingMax = 100;

    // Unified peak meter collapses min(L, R) and max(L, R) into a single weighted value so the
    // base bar and stereo overlay coincide and read as one solid bar. The weighting favors the
    // quieter channel by the bias multiplier: combined = (low * M + high) / (M + 1). A multiplier
    // of 0 falls back to plain max(L, R); 1 averages the channels; the default of 3 dampens
    // moment-to-moment stereo flutter without fully collapsing to min(L, R).
    public const int UnifiedMeterLowChannelBiasMultiplierDefault = 3;
    public const int UnifiedMeterLowChannelBiasMultiplierMax = 100;

    public bool UnifiedPeakMeter { get; set; } = false;

    private int _unifiedMeterLowChannelBiasMultiplier = UnifiedMeterLowChannelBiasMultiplierDefault;

    [XmlElement]
    public int UnifiedMeterLowChannelBiasMultiplier
    {
        get => _unifiedMeterLowChannelBiasMultiplier;
        set
        {
            int clamped = Math.Max(0, Math.Min(UnifiedMeterLowChannelBiasMultiplierMax, value));
            if (_unifiedMeterLowChannelBiasMultiplier == clamped) return;
            _unifiedMeterLowChannelBiasMultiplier = clamped;
        }
    }

    private int _meterPeakFps = MeterPeakFpsDefault;

    [XmlElement]
    public int MeterPeakFps
    {
        get => _meterPeakFps;
        set
        {
            int clamped = Math.Max(1, Math.Min(1000, value));
            if (_meterPeakFps == clamped) return;
            _meterPeakFps = clamped;
            MeterPeakFpsChanged?.Invoke();
        }
    }

    private int _meterPeakSampleRate = MeterPeakSampleRateDefault;

    [XmlElement]
    public int MeterPeakSampleRate
    {
        get => _meterPeakSampleRate;
        set
        {
            int clamped = Math.Max(1, Math.Min(1000, value));
            if (_meterPeakSampleRate == clamped) return;
            _meterPeakSampleRate = clamped;
            MeterPeakSampleRateChanged?.Invoke();
        }
    }

    private int _meterPeakChangeCeiling = MeterPeakChangeCeilingDefault;

    [XmlElement]
    public int MeterPeakChangeCeiling
    {
        get => _meterPeakChangeCeiling;
        set
        {
            int clamped = Math.Max(0, Math.Min(MeterPeakChangeCeilingMax, value));
            if (_meterPeakChangeCeiling == clamped) return;
            _meterPeakChangeCeiling = clamped;
        }
    }

    // App icon retry. AppIconResolver.Acquire() can return null for transient reasons - the most
    // common one is a cold shell-icon cache when a session is enumerated before Explorer has had
    // a chance to extract the app's icon at our target raster size. AudioSession retries up to
    // IconRetryAttempts times after the initial resolution; the wait between attempts grows
    // linearly: wait_n = n * IconRetryIntervalMs. With the default (250ms, 4 attempts) the worst-
    // case schedule is 0ms, +250ms, +500ms, +750ms - total ~1.5s before giving up.
    public const int IconRetryIntervalMsDefault = 250;
    public const int IconRetryIntervalMsMin = 50;
    public const int IconRetryIntervalMsMax = 5000;
    public const int IconRetryAttempts = 4;

    private int _iconRetryIntervalMs = IconRetryIntervalMsDefault;

    [XmlElement]
    public int IconRetryIntervalMs
    {
        get => _iconRetryIntervalMs;
        set
        {
            int clamped = Math.Max(IconRetryIntervalMsMin, Math.Min(IconRetryIntervalMsMax, value));
            if (_iconRetryIntervalMs == clamped) return;
            _iconRetryIntervalMs = clamped;
        }
    }

    // Bound for the icon-resolver's LRU "limbo" queue. When a cached icon's refcount drops to zero
    // (its last AudioSession is disposed) it sits in this queue and can be revived on the next
    // Acquire for the same app. When the queue overflows, the oldest dead entry is dropped from the
    // cache entirely. Default 10 keeps a small set of recently-departed apps warm so flipping
    // between apps in a session doesn't pay re-extraction. 0 = evict immediately.
    public const int IconLruLimitDefault = 10;
    public const int IconLruLimitMin = 0;
    public const int IconLruLimitMax = 1000;

    private int _iconLruLimit = IconLruLimitDefault;

    [XmlElement]
    public int IconLruLimit
    {
        get => _iconLruLimit;
        set
        {
            int clamped = Math.Max(IconLruLimitMin, Math.Min(IconLruLimitMax, value));
            if (_iconLruLimit == clamped) return;
            _iconLruLimit = clamped;
        }
    }

    private string _meterPeakColorHex = MeterPeakColorDefaultHex;

    [XmlElement]
    public string MeterPeakColorHex
    {
        get => _meterPeakColorHex;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? MeterPeakColorDefaultHex : value;
            if (_meterPeakColorHex == normalized) return;
            _meterPeakColorHex = normalized;
        }
    }

    private Color? _tempMeterPeakColor;

    [XmlIgnore]
    public Color? TemporaryMeterPeakColor
    {
        get => _tempMeterPeakColor;
        set
        {
            if (_tempMeterPeakColor == value) return;
            _tempMeterPeakColor = value;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Resolved meter peak color. Live preview wins over the persisted hex; both fall back to white
    /// so consumers (the brush in App.UpdateThemeResources) always see a real color.
    /// </summary>
    public Color EffectiveMeterPeakColor =>
        TemporaryMeterPeakColor ?? NullableThemeColor.ParseHexOrDefault(MeterPeakColorHex, Colors.White);

    private string _meterPeakStereoColorHex = MeterPeakStereoColorDefaultHex;

    [XmlElement]
    public string MeterPeakStereoColorHex
    {
        get => _meterPeakStereoColorHex;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? MeterPeakStereoColorDefaultHex : value;
            if (_meterPeakStereoColorHex == normalized) return;
            _meterPeakStereoColorHex = normalized;
        }
    }

    private Color? _tempMeterPeakStereoColor;

    [XmlIgnore]
    public Color? TemporaryMeterPeakStereoColor
    {
        get => _tempMeterPeakStereoColor;
        set
        {
            if (_tempMeterPeakStereoColor == value) return;
            _tempMeterPeakStereoColor = value;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Resolved stereo overlay color (drawn on top of the base bar to max(L, R)). Live preview
    /// wins over the persisted hex; both fall back to half-alpha white so consumers always see a
    /// real color.
    /// </summary>
    public Color EffectiveMeterPeakStereoColor =>
        TemporaryMeterPeakStereoColor
        ?? NullableThemeColor.ParseHexOrDefault(MeterPeakStereoColorHex, Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

    /// <summary>Raised when MeterPeakFps changes so AudioDeviceManager can retune its render interval.</summary>
    public event Action? MeterPeakFpsChanged;

    /// <summary>Raised when MeterPeakSampleRate changes so AudioDeviceManager can retune its sample interval.</summary>
    public event Action? MeterPeakSampleRateChanged;

    // Slider thumb. The built-in catalog (SliderThumbGlyphOption.CreateDefaults) is hardcoded and rebuilt
    // from scratch on every load, so the list itself is never serialized. Only the user's current selection
    // is persisted via the SliderThumb element below - and when that selection names a built-in, the built-in
    // wins; otherwise the loaded option is appended to the catalog so it stays in the dropdown.
    [XmlIgnore]
    public string SliderThumbGlyph { get; set; } = "Capsule";

    [XmlIgnore]
    public List<SliderThumbGlyphOption> SliderThumbOptions { get; set; } = [];

    [XmlElement("SliderThumb")]
    public SliderThumbGlyphOption? SerializedSliderThumb
    {
        get => SliderThumbOptions.FirstOrDefault(o => o.Name == SliderThumbGlyph);
        set => _loadedSliderThumb = value;
    }

    private SliderThumbGlyphOption? _loadedSliderThumb;

    // Tray icon interaction. Click actions are surfaced through TrayIconPage; the host wires what each
    // action does. The skeleton's TrayClickAction enum is a placeholder set extend it with app-specific
    // actions, then update App.xaml.cs's tray click handlers to dispatch on the chosen action.
    public bool TrayScrollEnabled { get; set; } = true;
    public TrayClickAction TrayDoubleClickAction { get; set; } = TrayClickAction.OpenSettings;
    public TrayClickAction TrayCtrlLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayCtrlRightClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltRightClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayCtrlDoubleLeftClickAction { get; set; } = TrayClickAction.Nothing;
    public TrayClickAction TrayAltDoubleLeftClickAction { get; set; } = TrayClickAction.Nothing;

    // Flyout undock/redock.
    // AllowFlyoutUndock is the master switch: when false, the undock button is hidden and any persisted
    // undocked state is force-redocked the next time the flyout opens, so disabling the feature never
    // strands a free-floating window with no way to redock it.
    // RestoreFlyoutUndockedOnStartup gates the single startup read of FlyoutUndocked; runtime undock /
    // redock writes still persist normally so flipping this back on resumes restoration.
    // FlyoutUndocked + FlyoutHasSavedPosition + FlyoutLeft / FlyoutTop are written on drag-release only,
    // never per-frame, so a drag doesn't saturate disk I/O.
    public bool AllowFlyoutUndock { get; set; } = true;
    public bool RestoreFlyoutUndockedOnStartup { get; set; } = true;
    public bool FlyoutUndocked { get; set; } = false;
    public bool FlyoutHasSavedPosition { get; set; } = false;
    public double FlyoutLeft { get; set; } = 0;
    public double FlyoutTop { get; set; } = 0;

    // Flyout device list. FlyoutDeviceLayout governs how each device's row stacks against its apps;
    // FlyoutDeviceSort orders the device list itself. ShowRecordingDevicesInFlyout is the flyout-side
    // gate for capture endpoints - it sits under the existing ShowRecordingDevices master so turning
    // recording off globally also hides them from the flyout. IntermixRecordingWithPlaybackInFlyout
    // controls whether render and capture devices interleave inside their state buckets or whether
    // capture devices group together at the top of the list.
    public FlyoutDeviceLayoutStyle FlyoutDeviceLayout { get; set; } = FlyoutDeviceLayoutStyle.AppsAboveDevice;
    public FlyoutDeviceSortOrder FlyoutDeviceSort { get; set; } = FlyoutDeviceSortOrder.StateGrouped;
    public bool ShowRecordingDevicesInFlyout { get; set; } = true;
    public bool IntermixRecordingWithPlaybackInFlyout { get; set; } = false;
    public bool ShowListenButtonInFlyout { get; set; } = true;

    // How the flyout marks actively-capturing app sessions inside a recording device's drawer.
    public CaptureActivityIndicator CaptureActivityIndicator { get; set; } = CaptureActivityIndicator.DimInactive;

    // Empty by default; defaults are seeded by EnsureDefaultHotkeys() after construction or load.
    // The previous in-place initializer collided with XmlSerializer's "append to existing list" behavior:
    // the deserializer adds <Binding> elements to the list returned by the getter, so any default
    // listed here would duplicate every time the saved settings.xml was reloaded.
    [XmlArray("Hotkeys")]
    [XmlArrayItem("Binding")]
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    // Raised when any setting is changed through the settings window.
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public AppSettings()
    {
        WireColorCallbacks();
        InitializeSliderThumbCatalog();
    }

    /// <summary>
    /// Seeds <see cref="SliderThumbOptions"/> from the built-in catalog, and, if a user-selected option was
    /// loaded from XML, either points <see cref="SliderThumbGlyph"/> at the matching built-in (by Name)
    /// or appends the loaded option to the catalog so it remains visible in the dropdown.
    /// </summary>
    public void InitializeSliderThumbCatalog()
    {
        List<SliderThumbGlyphOption> catalog = SliderThumbGlyphOption.CreateDefaults();

        if (_loadedSliderThumb is { } saved && !string.IsNullOrEmpty(saved.Name))
        {
            if (catalog.All(o => o.Name != saved.Name)) catalog.Add(saved);
            SliderThumbGlyph = saved.Name;
        }

        SliderThumbOptions = catalog;
    }

    /// <summary>
    /// Bridges every NullableThemeColor override on this instance to the global Changed event,
    /// so any color edit (committed hex or live-preview Temporary*) flows out through the same
    /// notification path as every other setting change.
    /// Idempotent: Unsubscribe runs first, so re-wiring after XmlSerializer replaces the ctor-wired
    /// instances post-deserialization can't double-fire.
    /// Specific listeners that want per-color granularity should attach via NullableThemeColor.Subscribe directly.
    /// </summary>
    public void WireColorCallbacks()
    {
        Action onChanged = RaiseChanged;
        foreach (NullableThemeColor color in EnumerateColorOverrides())
        {
            color.Unsubscribe(onChanged);
            color.Subscribe(onChanged);
        }
    }

    private IEnumerable<NullableThemeColor> EnumerateColorOverrides()
    {
        yield return TextColor;
        yield return BackgroundColor;
        yield return TrayIconColor;
    }

    public static string GetDefaultPath()
    {
        string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appFolder = Path.Combine(appDataFolder, Program.ApplicationName);
        Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "settings.xml");
    }

    // The folder that holds settings.xml - same folder as a LocalAppData install of the app.
    // Used by the uninstaller's "delete settings" branch.
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Program.ApplicationName);

    public void Save() => Save(GetDefaultPath());

    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            XmlSerializerNamespaces namespaces = new();
            namespaces.Add("", "");

            XmlWriterSettings writerSettings = new()
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Replace
            };

            using FileStream stream = new(path, FileMode.Create);
            using XmlWriter writer = XmlWriter.Create(stream, writerSettings);
            XmlSerializer serializer = new(typeof(AppSettings));
            serializer.Serialize(writer, this, namespaces);
        }
        catch
        {
            // best-effort
        }
    }

    public static AppSettings LoadOrDefault() => LoadOrDefault(GetDefaultPath());

    public static AppSettings LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using FileStream stream = new(path, FileMode.Open);
                XmlSerializer serializer = new(typeof(AppSettings));
                if (serializer.Deserialize(stream) is AppSettings loaded)
                {
                    // XmlSerializer replaces every NullableThemeColor property with a freshly-deserialized
                    // (parameterless-constructed) instance, dropping the ctor's wiring.
                    // Re-attach the bridge so loaded settings notify the global Changed event the same way
                    // fresh defaults do.
                    loaded.WireColorCallbacks();

                    // Build the slider-thumb dropdown catalog from the loaded selection (if any).
                    // Done after deserialization so SerializedSliderThumb has had a chance to land the saved option.
                    loaded.InitializeSliderThumbCatalog();

                    // One-time cleanup of duplicate hotkey rows that may have accumulated from a prior build
                    // that re-seeded the default hotkey on every launch.
                    // Top up any defaults missing from the persisted list (e.g. when a new build ships a new
                    // default action). Skips entries the user has tombstoned via the UI (RemovedByUser=true)
                    // so an explicit removal isn't undone on the next launch.
                    bool changed = loaded.DedupeHotkeysByIdentity();
                    changed |= loaded.EnsureDefaultHotkeys();
                    if (changed) loaded.Save(path);
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to default
        }
        AppSettings defaults = new();
        defaults.EnsureDefaultHotkeys();
        defaults.Save(path);
        return defaults;
    }

    /// <summary>
    /// The set of built-in hotkey bindings seeded for fresh installs and topped up on every launch.
    /// Identity is (Action, Parameter, BindingID): defaults always live on BindingID 0 (the primary row),
    /// so a user-added secondary binding (BindingID >= 1) for the same action does not block re-seeding
    /// the primary row.
    /// Skeleton ships with one illustrative binding; replace with your project's own defaults.
    /// </summary>
    private static IReadOnlyList<HotkeyBinding> CreateDefaultHotkeys() =>
    [
        new HotkeyBinding
        {
            Action = HotkeyAction.OpenSettings,
            Parameter = string.Empty,
            Modifiers = User32.MOD_CONTROL | User32.MOD_WIN | User32.MOD_ALT,
            VirtualKey = 0x53, // VK_S
            Enabled = true,
            BindingID = 0,
        },
    ];

    /// <summary>
    /// True if the binding occupies the same identity slot as one of the built-in defaults
    /// (same Action, Parameter, and BindingID). Used by the settings UI to decide whether removing
    /// a binding should hard-delete it or keep it as a tombstone (RemovedByUser=true) so the default
    /// doesn't reappear on the next launch.
    /// </summary>
    public static bool IsDefaultHotkeyIdentity(HotkeyAction action, string parameter, int bindingID)
    {
        foreach (HotkeyBinding d in CreateDefaultHotkeys())
            if (d.Matches(action, parameter, bindingID)) return true;
        return false;
    }

    /// <summary>
    /// Removes redundant hotkey rows that share the same identity tuple (Action, Parameter, BindingID),
    /// keeping the first occurrence.
    /// Returns true when at least one row was dropped (caller should persist).
    /// </summary>
    public bool DedupeHotkeysByIdentity()
    {
        HashSet<(HotkeyAction, string, int)> seen = [];
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < Hotkeys.Count; readIndex++)
        {
            HotkeyBinding b = Hotkeys[readIndex];
            (HotkeyAction, string, int) key = (b.Action, b.Parameter ?? string.Empty, b.BindingID);
            if (!seen.Add(key)) continue;

            if (writeIndex != readIndex) Hotkeys[writeIndex] = b;
            writeIndex++;
        }
        if (writeIndex == Hotkeys.Count) return false;

        Hotkeys.RemoveRange(writeIndex, Hotkeys.Count - writeIndex);
        return true;
    }

    /// <summary>
    /// Adds any built-in default hotkey bindings that aren't already represented in Hotkeys.
    /// "Represented" means: an existing entry with the same (Action, Parameter, BindingID) - including
    /// tombstoned entries with RemovedByUser=true - so a user who has explicitly removed a default
    /// is not re-seeded.
    /// Returns true when at least one default was newly added (caller should persist).
    /// </summary>
    public bool EnsureDefaultHotkeys()
    {
        bool added = false;
        foreach (HotkeyBinding d in CreateDefaultHotkeys())
        {
            bool present = false;
            foreach (HotkeyBinding existing in Hotkeys)
            {
                if (!existing.Matches(d.Action, d.Parameter, d.BindingID)) continue;

                present = true;
                break;
            }
            if (present) continue;

            Hotkeys.Add(new HotkeyBinding
            {
                Action = d.Action,
                Parameter = d.Parameter,
                Modifiers = d.Modifiers,
                VirtualKey = d.VirtualKey,
                Enabled = d.Enabled,
                BindingID = d.BindingID,
            });
            added = true;
        }
        return added;
    }
}
