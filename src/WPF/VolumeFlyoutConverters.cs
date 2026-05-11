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
/// Maps a bool (typically VolumeFlyoutCell.IsLast) to either the shared CornerRadiusFooterBottom
/// (rounded bottom corners) or zero. Lets the bottom cell of the device stack pick up the same
/// rounded-corner treatment the old monolithic footer Border used to apply unconditionally.
/// Reads from the application resources every time so a runtime EnableRoundedCorners flip flows
/// through without re-binding.
/// </summary>
internal sealed class IsLastToFooterCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isLast = value is true;
        if (!isLast) return new CornerRadius(0);

        object? resource = System.Windows.Application.Current?.Resources["CornerRadiusFooterBottom"];
        return resource is CornerRadius radius ? radius : new CornerRadius(0, 0, 8, 8);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
