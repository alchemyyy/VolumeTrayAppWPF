using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VolumeTrayAppWPF.Audio;
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
        return value switch
        {
            float f => Math.Round(f * 100.0),
            double d => Math.Round(d * 100.0),
            _ => 0.0
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Slider.Value comes through as double; clamp before division so a runaway DP value
        // can't push the underlying ISimpleAudioVolume call out of [0,1] and trip an HRESULT.
        double v = System.Convert.ToDouble(value, culture) / 100.0;
        v = v switch
        {
            < 0 => 0,
            > 1 => 1,
            _ => v
        };
        return (float)v;
    }
}

/// <summary>
/// Selects the glyph shown on the per-row mute button.
///
/// Render endpoints: muted shows PLAYBACK_VOLUME_MUTE; otherwise stays on a fixed
/// PLAYBACK_VOLUME_LOW. Volume-tier-aware selection is intentionally skipped here so the mute
/// button doesn't visually re-flow through tiers as the user drags the slider - GetVolumeTier
/// is reserved for the tray icon.
/// Capture endpoints: microphone-themed glyphs. Precedence muted > listening > sleeping > plain.
/// Sleeping (no app is actively streaming the mic so Windows idles the capture engine) ranks
/// below listening because Listen-to-this-device itself keeps a capture client running, so the
/// engine isn't actually asleep in that case.
///
/// MultiBinding inputs (in declared order):
///   [0]=Volume(scalar), [1]=IsMuted, [2]=IsCaptureDevice, [3]=IsListeningToThisDevice,
///   [4]=IsCaptureSleeping.
/// Volume is bound for parity with the binding contract but is not consulted in the conversion.
/// </summary>
internal sealed class VolumeGlyphConverter : IMultiValueConverter
{
    public static readonly VolumeGlyphConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return GlyphCatalog.PLAYBACK_VOLUME_LOW;

        bool muted = values[1] is true;
        bool isCapture = values.Length > 2 && values[2] is true;
        bool isListening = values.Length > 3 && values[3] is true;
        bool isSleeping = values.Length > 4 && values[4] is true;

        if (isCapture)
        {
            if (muted) return GlyphCatalog.MICROPHONE_OFF;
            if (isListening) return GlyphCatalog.MICROPHONE_LISTENING;
            if (isSleeping) return GlyphCatalog.MICROPHONE_SLEEP;
            return GlyphCatalog.MICROPHONE;
        }

        return muted ? GlyphCatalog.PLAYBACK_VOLUME_MUTE : GlyphCatalog.PLAYBACK_VOLUME_LOW;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Picks the glyph that paints on the device-icon button in the flyout footer. Precedence:
/// disabled > default > default-comms > enabled. Disabled wins because a disabled default device
/// (unusual but reachable) shouldn't keep showing the default glyph - the glyph is meant to read
/// as a state badge at a glance, and "this device isn't currently usable" trumps "this device
/// would be the default if it were on".
/// Default wins over default-comms on a device that holds both roles, so the multimedia identity
/// reads first; comms-only devices still surface their own glyph distinctly.
/// MultiBinding inputs (in declared order): IsActive, IsDefault, IsDefaultCommunications. Bound this
/// way so external state flips (mmsys.cpl enable / disable, default-device changes from another app)
/// re-trigger the converter through the standard PropertyChanged path - a Binding . here would only
/// re-evaluate on DataContext replacement and miss in-place state mutations.
/// </summary>
internal sealed class DeviceIconGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool isActive = values.Length > 0 && values[0] is true;
        bool isDefault = values.Length > 1 && values[1] is true;
        bool isDefaultComms = values.Length > 2 && values[2] is true;

        if (!isActive) return GlyphCatalog.PLAYBACK_DEVICE_DISABLED;
        if (isDefault) return GlyphCatalog.PLAYBACK_DEVICE_DEFAULT;
        if (isDefaultComms) return GlyphCatalog.PLAYBACK_DEVICE_DEFAULT_COMMS;
        return GlyphCatalog.PLAYBACK_DEVICE_ENABLED;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Dims the device icon when the bound device is in any non-active state (Disabled / Unplugged /
/// NotPresent). The visual delta pairs with the glyph so the user reads "this device is here but
/// not currently usable". Bound to AudioDevice.IsActive directly so external state changes
/// re-evaluate the binding immediately.
/// </summary>
internal sealed class DeviceIconOpacityConverter : IValueConverter
{
    private const double DimmedOpacity = 0.4;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isActive = value is true;
        return isActive ? 1.0 : DimmedOpacity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Builds the composite text drawn under each device's name. Combines two independent toggles
/// (ShowDeviceFormatText, ShowDeviceCodecText) with the device-side inputs to emit one of four
/// shapes:
///   format on, codec on, BT device with codec  -> "FORMAT, CODEC"
///   format on, codec off (or non-BT, or codec empty) -> "FORMAT"
///   format off, codec on, BT device with codec -> "CODEC"
///   format off, codec off (or both inputs empty) -> ""  (Canvas visibility collapses on empty)
///
/// MultiBinding inputs (in declared order):
///   [0]=DefaultFormat (string?)  [1]=IsBluetooth (bool)  [2]=CurrentCodecName (string)
///   [3]=ShowDeviceFormatText (bool)  [4]=ShowDeviceCodecText (bool)
/// </summary>
internal sealed class DeviceFormatLineTextConverter : IMultiValueConverter
{
    public static readonly DeviceFormatLineTextConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 5) return string.Empty;

        string format = values[0] as string ?? string.Empty;
        bool isBluetooth = values[1] is true;
        string codec = values[2] as string ?? string.Empty;
        bool showFormat = values[3] is true;
        bool showCodec = values[4] is true;

        bool formatShown = showFormat && format.Length > 0;
        bool codecShown = showCodec && isBluetooth && codec.Length > 0;

        if (formatShown && codecShown) return format + ", " + codec;
        if (formatShown) return format;
        if (codecShown) return codec;
        return string.Empty;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visibility companion to <see cref="DeviceFormatLineTextConverter"/>. Mirrors its activation
/// rules and returns Visible whenever the converter would emit a non-empty string. Used on the
/// hosting Canvas so the format strip collapses cleanly when both lines are suppressed (no row
/// metric shift either way - the Canvas is zero-measure).
///
/// Inputs match <see cref="DeviceFormatLineTextConverter"/> exactly.
/// </summary>
internal sealed class DeviceFormatLineVisibilityConverter : IMultiValueConverter
{
    public static readonly DeviceFormatLineVisibilityConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 5) return Visibility.Collapsed;

        string format = values[0] as string ?? string.Empty;
        bool isBluetooth = values[1] is true;
        string codec = values[2] as string ?? string.Empty;
        bool showFormat = values[3] is true;
        bool showCodec = values[4] is true;

        bool formatShown = showFormat && format.Length > 0;
        bool codecShown = showCodec && isBluetooth && codec.Length > 0;

        return (formatShown || codecShown) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Y offset (positive = down) applied via TranslateTransform to the device title band when the
/// FlyoutDeviceTitlePosition setting is AboveSlider. Anchors the title's visual bottom to the top
/// of the percent textblock (the volume-level indicator) so the title sits flush against the
/// number rather than against the row boundary. Returns 0 in the BelowSlider configuration so the
/// title stays in its natural Row=1 position.
///
/// MultiBinding inputs (in declared order):
///   [0]=DeviceTitleRowIndex (0 = AboveSlider, 1 = BelowSlider)
///   [1]=DeviceSliderRow.ActualHeight (the row hosting the slider + percent text)
///   [2]=DeviceVolumePercent.ActualHeight (the percent readout textblock)
/// The percent text is vertically centered inside the slider row, so its top sits at
/// (sliderRow - percent) / 2 from the slider row's top edge - the exact distance the title
/// needs to translate down so its bottom edge meets that line.
/// </summary>
internal sealed class TitleRowVerticalOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;
        if (values[0] is not int rowIndex) return 0.0;
        if (rowIndex != 0) return 0.0;

        double sliderRowHeight = values[1] is double sh ? sh : 0.0;
        double percentHeight = values[2] is double ph ? ph : 0.0;
        double delta = (sliderRowHeight - percentHeight) / 2.0;
        return delta > 0 ? delta : 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

