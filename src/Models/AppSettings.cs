using System.IO;
using System.Xml;
using System.Xml.Serialization;
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
/// Which Windows surface the flyout's Sound-settings titlebar button opens.
/// LegacySoundPanel: classic mmsys.cpl Sound control panel (the floating window with Playback /
/// Recording / Sounds / Communications tabs).
/// WindowsSettingsApp: the modern Settings app's System > Sound page (ms-settings:sound).
/// </summary>
public enum SoundSettingsTarget
{
    LegacySoundPanel,
    WindowsSettingsApp,
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
/// Where the device's title + control-buttons band sits relative to its slider.
/// BelowSlider (default): slider on top, name and per-device action buttons underneath as footer chrome.
/// AboveSlider: name and per-device action buttons render on top, slider underneath.
/// Independent of FlyoutDeviceLayoutStyle, which governs the device-row vs apps stacking.
/// </summary>
public enum FlyoutDeviceTitlePosition
{
    BelowSlider,
    AboveSlider,
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
/// Visibility rule for the titlebar communications-activity button.
/// AlwaysShow: button always rendered in the header cluster.
/// WhenDuckingOn (default): button only rendered when UserDuckingPreference is set to any active
///                mode (mute / 80% / 50%); hidden when "Do nothing" is selected.
/// Hidden: button never rendered; the registry watcher also stays asleep.
/// </summary>
public enum CommunicationsButtonVisibility
{
    AlwaysShow,
    WhenDuckingOn,
    Hidden,
}

/// <summary>
/// Visual treatment that flags which apps in a recording device's drawer are currently capturing
/// from the microphone (their session State is Active).
/// DimInactive (default): icons of non-capturing apps are dimmed, matching how disabled devices are dimmed.
/// ActiveGlyph: a small overlay glyph is stamped on the icons of actively capturing apps; non-capturers untouched.
/// HideInactive: non-capturing app rows are collapsed entirely, so only actively-capturing apps remain visible.
/// None: no visual indication.
/// </summary>
public enum CaptureActivityIndicator
{
    DimInactive,
    ActiveGlyph,
    HideInactive,
    None,
}

/// <summary>
/// How the per-device app drawer renders its session list.
/// Sliders: full row per app, icon + volume slider + percent text (the original layout).
/// Icons: icons only, packed into an 8-column grid -- pointless to show volume mixer sliders for
/// recording devices since they don't go through a mixing layer.
/// </summary>
public enum AppDrawerDisplayType
{
    Sliders,
    Icons,
}

/// <summary>
/// Stack flow for the grid drawer's app icons. The first four are explicit directions:
///   TopBottom -- horizontal rows, filled top-down (the original layout).
///   BottomTop -- horizontal rows, filled bottom-up so the first item sits closest to the device row.
///   LeftRight -- vertical columns, filled left-to-right.
///   RightLeft -- vertical columns, filled right-to-left.
/// Auto picks BottomTop when apps sit above the device row (so the first app abuts the device) and
/// TopBottom when apps sit below it. The AppDrawerIconsPerRow setting caps the primary-axis group:
/// items-per-row in the horizontal modes, items-per-column in the vertical ones.
/// </summary>
public enum AppDrawerStackDirection
{
    TopBottom,
    BottomTop,
    LeftRight,
    RightLeft,
    Auto,
}

/// <summary>
/// How the icon grid anchors its trailing partial row (or partial column, in vertical-flow stack
/// directions).
///   Off            -- partial group hugs the left / top edge, full rows always left-anchored.
///   Centered       -- partial group is centered along the cross axis; full rows still left-anchored.
///   CenteredSoftMax -- partial group is left-anchored at the position a centered "soft-max"-icon
///                      row would occupy, so icons don't shift as the row grows from 1 up to soft-max.
///                      Past the soft-max count, the row switches to fully centered behavior.
/// </summary>
public enum AppDrawerIconsCenterMode
{
    Off,
    Centered,
    CenteredSoftMax,
}

/// <summary>
/// Root application settings class.
/// Skeleton scaffold with a few illustrative fields - extend with project-specific settings in your fork.
///
/// Range / default conventions:
///   Every clamped numeric setting exposes a public const triple <c>XxxMin</c> / <c>XxxMax</c> /
///   <c>XxxDefault</c>. The same consts are referenced in three places: the field initializer
///   (default), the property setter (clamp), and the XAML spinner via <c>{x:Static models:AppSettings.XxxMin}</c>.
///   Adding a new clamped numeric should follow this pattern - no magic literals at call sites.
///
/// Change notification:
///   The settings page code-behind calls <c>RaiseChanged()</c> after each user-driven write
///   (`saveAndNotify`), so the global <see cref="Changed"/> event covers persistence and
///   brush-rebuild for normal UI edits. Per-property events exist where a single consumer needs
///   fine-grained granularity: <see cref="MeterPeakFpsChanged"/> / <see cref="MeterPeakSampleRateChanged"/>
///   feed <c>AudioDeviceManager</c> retune logic, and fire from inside their setters.
///   Computed projections (<see cref="EffectiveMeterPeakColor"/>, <see cref="EffectiveMeterPeakStereoColor"/>)
///   have hex setters that DO RaiseChanged on every write so DynamicResource consumers re-resolve -
///   this is the WPF binding-dot rule (derived projections need notifications on every input).
///   The Temporary*Color live-preview setters also RaiseChanged for the same reason.
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

    // Default expanded / collapsed state for a device's app drawer. Only consulted for devices
    // without a persisted per-device override in devices.xml; once the user toggles a specific
    // device's chevron the per-device entry wins. Default true to preserve the original behavior.
    public bool DefaultAppDrawerExpanded { get; set; } = true;

    // Persisted "last-seen active default" id per role / flow. AudioDeviceManager writes these
    // every time GetDefaultAudioEndpoint returns a real device, and reads them as a fallback
    // when the same lookup later comes back null - that null result, while a previously-default
    // device still exists in the device list, means the user disabled the active default and
    // Windows had no other active device of that role / flow to promote. The fallback restores
    // IsDefault on the disabled wrapper so the visibility filter under the
    // ShowDefault*EvenIfDisabled toggles has a target to act on.
    public string? LastKnownDefaultPlaybackDeviceID { get; set; }
    public string? LastKnownDefaultCommsPlaybackDeviceID { get; set; }
    public string? LastKnownDefaultRecordingDeviceID { get; set; }
    public string? LastKnownDefaultCommsRecordingDeviceID { get; set; }

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
    // Audible feedback on playback-device slider changes only (not per-app sliders, not capture devices).
    // Plays the same wav per-app feedback uses but routed through the specific render endpoint the user
    // just adjusted (WASAPI shared mode), so the ding comes out of that device instead of the system
    // default. Fires on mouse-up after a click/drag and on each wheel notch over the device row.
    public bool PlayDeviceVolumeChangeSound { get; set; } = true;
    // Gate on top of PlayDeviceVolumeChangeSound: skip the ding when the device is already rendering
    // audio (peak meter > 0 at play-time). Keeps the beep out of music / calls / games where it would
    // just step on the existing audio. Checked right before PlayChangeFeedback, after the dwell, so
    // the reading reflects "is anything playing right now" rather than the gesture's leading edge.
    public bool SuppressDeviceVolumeChangeSoundWhenAudioPlaying { get; set; } = true;

    // Noise floor for the suppression gate above, expressed as 0..100 percent of full scale on the
    // smoothed peak meter (PeakValueMax). Suppression triggers only when the meter EXCEEDS this
    // value, so 0 reproduces the original "any audio at all" behavior and 100 effectively disables
    // suppression. Clamped to [Min, Max] in the setter so a corrupt settings.xml can't drift the
    // gate outside what the spinner allows.
    public const int DingSuppressionPeakThresholdPercentDefault = 5;
    public const int DingSuppressionPeakThresholdPercentMin = 0;
    public const int DingSuppressionPeakThresholdPercentMax = 100;

    private int _dingSuppressionPeakThresholdPercent = DingSuppressionPeakThresholdPercentDefault;

    [XmlElement]
    public int DingSuppressionPeakThresholdPercent
    {
        get => _dingSuppressionPeakThresholdPercent;
        set
        {
            int clamped = Math.Max(
                DingSuppressionPeakThresholdPercentMin,
                Math.Min(DingSuppressionPeakThresholdPercentMax, value));
            if (_dingSuppressionPeakThresholdPercent == clamped) return;
            _dingSuppressionPeakThresholdPercent = clamped;
        }
    }
    // Same idea for per-app sliders. The wav plays through this app's audio session at MediaPlayer.Volume
    // scaled to the target app's slider value, so the feedback's loudness matches what the user just dialed
    // the app to. Caveat: it isn't injected into the target app's session - if the user has muted/lowered
    // VolumeTrayApp itself, the feedback gets attenuated again on top of that scalar.
    public bool PlayAppVolumeChangeSound { get; set; } = true;

    // Context menu
    public ContextMenuPosition ContextMenuPosition { get; set; } = ContextMenuPosition.Modern;

    // Tray context-menu font size. Drives all text in the menu; every other element scales relative
    // to font size, so this is effectively the menu zoom level.
    public const int ContextMenuFontSizeDefault = 15;
    public const int ContextMenuFontSizeMin = 8;
    public const int ContextMenuFontSizeMax = 48;
    public int ContextMenuFontSize { get; set; } = ContextMenuFontSizeDefault;

    // Per-flow device-name style for the tray context menu rows. Defaults to Everything so the
    // initial UX matches the prior behavior (full Windows FriendlyName).
    public TrayMenuDeviceNameStyle TrayMenuPlaybackDeviceNameStyle { get; set; } = TrayMenuDeviceNameStyle.NameAndModel;
    public TrayMenuDeviceNameStyle TrayMenuRecordingDeviceNameStyle { get; set; } = TrayMenuDeviceNameStyle.NameAndModel;

    // Cap on the rendered device-name length in the tray context menu. When the chosen name slice
    // exceeds this character count, the suffix is replaced with a 2-dot ellipsis ("..") to keep
    // the menu width predictable. Clamped to the spinner's [Min, Max] range so a corrupt
    // settings.xml can't push the value outside what the UI accepts.
    public const int TrayMenuDeviceNameMaxLengthDefault = 32;
    public const int TrayMenuDeviceNameMaxLengthMin = 3;
    public const int TrayMenuDeviceNameMaxLengthMax = 200;

    private int _trayMenuDeviceNameMaxLength = TrayMenuDeviceNameMaxLengthDefault;

    [XmlElement]
    public int TrayMenuDeviceNameMaxLength
    {
        get => _trayMenuDeviceNameMaxLength;
        set
        {
            int clamped = Math.Max(
                TrayMenuDeviceNameMaxLengthMin,
                Math.Min(TrayMenuDeviceNameMaxLengthMax, value));
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
    public const int MeterPeakFpsMin = 1;
    public const int MeterPeakFpsMax = 1000;

    public const int MeterPeakSampleRateDefault = 90;
    public const int MeterPeakSampleRateMin = 1;
    public const int MeterPeakSampleRateMax = 1000;

    // Per-redraw ceiling, in 0-100 volume units, on how far VolumeSlider's rendered peak can move
    // toward the incoming smoothed target. Caps single-frame jumps so a sudden silence-to-loud
    // (or loud-to-silence) transition ramps over a few frames instead of teleporting. 0 freezes
    // the meter; 100 disables the clamp (one-tick catch-up).
    public const int MeterPeakChangeCeilingDefault = 9;
    public const int MeterPeakChangeCeilingMin = 0;
    public const int MeterPeakChangeCeilingMax = 100;

    // Unified peak meter collapses min(L, R) and max(L, R) into a single weighted value so the
    // base bar and stereo overlay coincide and read as one solid bar. The weighting favors the
    // quieter channel by the bias multiplier: combined = (low * M + high) / (M + 1). A multiplier
    // of 0 falls back to plain max(L, R); 1 averages the channels; the default of 3 dampens
    // moment-to-moment stereo flutter without fully collapsing to min(L, R).
    public const int UnifiedMeterLowChannelBiasMultiplierDefault = 3;
    public const int UnifiedMeterLowChannelBiasMultiplierMin = 0;
    public const int UnifiedMeterLowChannelBiasMultiplierMax = 100;

    public bool UnifiedPeakMeter { get; set; } = true;

    private int _unifiedMeterLowChannelBiasMultiplier = UnifiedMeterLowChannelBiasMultiplierDefault;

    [XmlElement]
    public int UnifiedMeterLowChannelBiasMultiplier
    {
        get => _unifiedMeterLowChannelBiasMultiplier;
        set
        {
            int clamped = Math.Max(
                UnifiedMeterLowChannelBiasMultiplierMin,
                Math.Min(UnifiedMeterLowChannelBiasMultiplierMax, value));
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
            int clamped = Math.Max(MeterPeakFpsMin, Math.Min(MeterPeakFpsMax, value));
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
            int clamped = Math.Max(MeterPeakSampleRateMin, Math.Min(MeterPeakSampleRateMax, value));
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
            int clamped = Math.Max(
                MeterPeakChangeCeilingMin,
                Math.Min(MeterPeakChangeCeilingMax, value));
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
            // Fire Changed so EffectiveMeterPeakColor's DynamicResource consumers re-resolve.
            // Without this, a programmatic hex write goes unnoticed by the brush rebuild path
            // (the WPF binding-dot trap: derived projections need notifications on every input).
            RaiseChanged();
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
            // See MeterPeakColorHex setter: derived EffectiveMeterPeakStereoColor needs notification.
            RaiseChanged();
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
    public TrayClickAction TrayDoubleClickAction { get; set; } = TrayClickAction.Nothing;
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
    public bool FlyoutUndocked { get; set; } = true;
    public bool FlyoutHasSavedPosition { get; set; } = false;
    public double FlyoutLeft { get; set; } = 0;
    public double FlyoutTop { get; set; } = 0;

    // Flyout chrome layout. Flips the title-bar row (Settings cluster + Undock button) between the
    // top of the flyout (default) and the bottom. Visual only - clamping in PositionNearTray is
    // layout-agnostic and re-resolves the SettingsButton offset via TransformToAncestor.
    public bool FlyoutHeaderAtBottom { get; set; } = false;

    // Where the flyout's Sound-settings titlebar button routes. LegacySoundPanel opens mmsys.cpl
    // (the classic Sound panel) and matches the historical Windows experience; WindowsSettingsApp
    // routes to ms-settings:sound for users who prefer the modern Settings UI.
    public SoundSettingsTarget SoundSettingsTarget { get; set; } = SoundSettingsTarget.LegacySoundPanel;

    // Flyout device list. FlyoutDeviceLayout governs how each device's row stacks against its apps;
    // FlyoutDeviceSort orders the device list itself. ShowRecordingDevicesInFlyout is the flyout-side
    // gate for capture endpoints - it sits under the existing ShowRecordingDevices master so turning
    // recording off globally also hides them from the flyout. IntermixRecordingWithPlaybackInFlyout
    // controls whether render and capture devices interleave inside their state buckets or whether
    // capture devices group together at the top of the list.
    public FlyoutDeviceLayoutStyle FlyoutDeviceLayout { get; set; } = FlyoutDeviceLayoutStyle.AppsAboveDevice;
    public FlyoutDeviceTitlePosition FlyoutDeviceTitlePosition { get; set; } = FlyoutDeviceTitlePosition.BelowSlider;
    public FlyoutDeviceSortOrder FlyoutDeviceSort { get; set; } = FlyoutDeviceSortOrder.StateGrouped;
    public bool ShowRecordingDevicesInFlyout { get; set; } = true;
    public bool IntermixRecordingWithPlaybackInFlyout { get; set; } = false;

    // Titlebar communications-activity button visibility. Drives both the button's Visibility and
    // whether the registry watcher even runs - Hidden keeps the watcher asleep entirely.
    public CommunicationsButtonVisibility FlyoutCommunicationsButtonVisibility { get; set; }
        = CommunicationsButtonVisibility.WhenDuckingOn;

    // Per-device-row control-button visibility. One pair per button - playback rows read the *ForPlayback
    // flag, recording rows read the *ForRecording flag. The Listen button is capture-only by nature, so
    // only the recording flag exists; toggling it off hides the listen glyph on recording rows.
    // Default-device and Battery buttons ship on; Lock, EqualizerAPO, and Listen ship off as they are
    // power-user features the typical user never reaches for.
    public bool ShowLockButtonForPlayback { get; set; } = false;
    public bool ShowEqualizerAPOButtonForPlayback { get; set; } = false;
    public bool ShowDefaultDeviceButtonForPlayback { get; set; } = true;
    public bool ShowBatteryButtonForPlayback { get; set; } = true;
    public bool ShowLockButtonForRecording { get; set; } = false;
    public bool ShowEqualizerAPOButtonForRecording { get; set; } = false;
    public bool ShowListenButtonForRecording { get; set; } = false;
    public bool ShowDefaultDeviceButtonForRecording { get; set; } = true;
    public bool ShowBatteryButtonForRecording { get; set; } = true;

    // Compact PKEY_AudioEngine_DeviceFormat readout under the device name. On by default - shows the
    // current sample-rate / bit-depth / channel layout in a compact strip under the device name.
    // Toggling on / off just shows / collapses the Canvas; no row metrics shift since the Canvas is
    // already zero-measure.
    public bool ShowDeviceFormatText { get; set; } = true;

    // Suffix the format readout strip with the live Bluetooth A2DP codec on BT-flagged devices.
    // Independent from ShowDeviceFormatText: with format off and codec on, the codec name renders
    // alone on the strip for BT devices (non-BT devices stay collapsed). Same diagnostic-info
    // tier as the format readout itself, so the default is off.
    public bool ShowDeviceCodecText { get; set; } = false;

    // How the flyout marks actively-capturing app sessions inside a recording device's drawer.
    public CaptureActivityIndicator CaptureActivityIndicator { get; set; } = CaptureActivityIndicator.ActiveGlyph;

    // Drawer style for the per-app session list under a recording device. Defaults to Icons because
    // recording sessions don't have a per-app volume mixer to control; the user can flip back to
    // Sliders for visual consistency with the playback drawers.
    public AppDrawerDisplayType RecordingAppDrawerDisplayType { get; set; } = AppDrawerDisplayType.Icons;

    // Icon-grid sub-options. Center mode picks how a partial trailing row reads: Off keeps it
    // left-anchored; Centered shifts it to the cross-axis center; CenteredSoftMax left-anchors it
    // at the position a centered soft-max-icon row would occupy (so icons don't visibly shift as
    // the row grows from 1 up to soft-max), then crosses over to fully centered once exceeded.
    // Scale is an integer percent applied to the icon visuals (Image + fallback / mute glyphs) so
    // the user can bump them larger without changing the slot grid. Defaults to 115 so icons read
    // a touch larger than the slider-drawer baseline.
    // Soft-max + icons-per-row defaults / clamps are exposed as consts so the WPF panel DP, the
    // Window-side sanitiser, and the property initializer all reference one source of truth.
    public const int AppDrawerIconsCenterSoftMaxDefault = 7;
    public const int AppDrawerIconsCenterSoftMaxMin = 1;
    public const int AppDrawerIconsCenterSoftMaxMax = 16;
    public AppDrawerIconsCenterMode AppDrawerIconsCenterMode { get; set; } = AppDrawerIconsCenterMode.Off;
    public int AppDrawerIconsCenterSoftMax { get; set; } = AppDrawerIconsCenterSoftMaxDefault;

    // Integer percent applied to grid-drawer icon visuals. 100 = baseline; default reads a touch
    // larger than the slider-drawer reference. Range spans 50..200 to keep icons readable at both ends.
    public const int AppDrawerIconScalePercentDefault = 110;
    public const int AppDrawerIconScalePercentMin = 50;
    public const int AppDrawerIconScalePercentMax = 200;
    public int AppDrawerIconScalePercent { get; set; } = AppDrawerIconScalePercentDefault;

    // Maximum icons per row in the grid drawer. The slot grid auto-shrinks the cell width when this
    // exceeds 8 so the grid stays inside the drawer's inner band; below 8 the slot stays at 40 and
    // the grid is just visually narrower.
    // In vertical stack-direction modes (LeftRight / RightLeft) this same value caps icons per
    // column instead -- one knob covers both axes.
    public const int AppDrawerIconsPerRowDefault = 9;
    public const int AppDrawerIconsPerRowMin = 1;
    public const int AppDrawerIconsPerRowMax = 16;
    public int AppDrawerIconsPerRow { get; set; } = AppDrawerIconsPerRowDefault;

    // Stack direction for the icon grid. Auto resolves at render time against FlyoutDeviceLayout so
    // the first app always sits closest to its device row regardless of which side the apps are on.
    public AppDrawerStackDirection AppDrawerStackDirection { get; set; } = AppDrawerStackDirection.Auto;

    // Per-device-type, per-drawer-mode caps on how many app rows render before the drawer enters
    // overflow scroll. Sliders caps slider rows (each app = one row); Icons caps icon-grid rows.
    // Four distinct values so a user can tune each axis without one knob bleeding into another --
    // even though playback is currently hard-wired to Sliders, the Icons cap is still stored for
    // future symmetry. Defaults: 24 slider rows / 10 icon rows match the heaviest usable density
    // before a typical flyout exceeds the screen.
    public const int AppDrawerSlidersMaxAppsDefault = 24;
    public const int AppDrawerSlidersMaxAppsMin = 1;
    public const int AppDrawerSlidersMaxAppsMax = 200;
    public const int AppDrawerIconsMaxRowsDefault = 10;
    public const int AppDrawerIconsMaxRowsMin = 1;
    public const int AppDrawerIconsMaxRowsMax = 200;

    public int PlaybackAppDrawerSlidersMaxApps { get; set; } = AppDrawerSlidersMaxAppsDefault;
    public int PlaybackAppDrawerIconsMaxRows { get; set; } = AppDrawerIconsMaxRowsDefault;
    public int RecordingAppDrawerSlidersMaxApps { get; set; } = AppDrawerSlidersMaxAppsDefault;
    public int RecordingAppDrawerIconsMaxRows { get; set; } = AppDrawerIconsMaxRowsDefault;

    // Auto-update
    // CheckForUpdatesEnabled gates the background poll loop entirely. ShowUpdateNotificationsEnabled
    // controls whether a "new version available" tray balloon fires while the flyout is closed;
    // ShowUpdateButtonInFlyout drives the floating "Update!" affordance in the flyout's header row.
    // UpdateCheckIntervalMs is the polling cadence (defaulted to 1h; clamped to the [Min, Max] floor
    // and ceiling defined in TimeConstants on each poll).
    public bool CheckForUpdatesEnabled { get; set; } = true;
    public bool ShowUpdateNotificationsEnabled { get; set; } = false;
    public bool ShowUpdateButtonInFlyout { get; set; } = true;
    public int UpdateCheckIntervalMs { get; set; } = TimeConstants.UpdateCheckIntervalDefaultMs;

    // Empty by default; defaults are seeded by HotkeyDefaults.EnsureDefaults after construction or load.
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
                    bool changed = HotkeyDefaults.DedupeByIdentity(loaded.Hotkeys);
                    changed |= HotkeyDefaults.EnsureDefaults(loaded.Hotkeys);
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
        HotkeyDefaults.EnsureDefaults(defaults.Hotkeys);
        defaults.Save(path);
        return defaults;
    }

}
