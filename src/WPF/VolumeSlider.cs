using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Slider subclass that drives a two-layer peak-meter overlay sized by separate
/// <see cref="PeakValueMin"/> and <see cref="PeakValueMax"/> dependency properties. The control
/// template (see VolumeFlyout.xaml) ships two named Borders: "MeterPeak" (the base bar painted
/// to min(L, R)) and "MeterPeakStereo" (the overlay painted on top to max(L, R)). Both widths
/// are recomputed here from the corresponding peak value, the slider's Value, the thumb's actual
/// width, and the bar's corner radius. PeakValue* are in [0, 1]; at peak=1 the bar's right edge
/// extends one corner-radius past the thumb's left edge so the rounded cap's apex aligns with it.
/// Rate-limiting (the user-facing MeterPeakChangeCeiling) lives in MeterLerp's render-tick, not
/// here - a downstream rate limiter would only advance when PropertyChanged fired and would get
/// stuck arbitrarily far from target once the lerp converged.
/// </summary>
internal sealed class VolumeSlider : Slider
{
    public static readonly DependencyProperty PeakValueMinProperty = DependencyProperty.Register(
        nameof(PeakValueMin),
        typeof(float),
        typeof(VolumeSlider),
        new PropertyMetadata(0f, OnPeakValueChanged));

    public static readonly DependencyProperty PeakValueMaxProperty = DependencyProperty.Register(
        nameof(PeakValueMax),
        typeof(float),
        typeof(VolumeSlider),
        new PropertyMetadata(0f, OnPeakValueChanged));

    public float PeakValueMin
    {
        get => (float)GetValue(PeakValueMinProperty);
        set => SetValue(PeakValueMinProperty, value);
    }

    public float PeakValueMax
    {
        get => (float)GetValue(PeakValueMaxProperty);
        set => SetValue(PeakValueMaxProperty, value);
    }

    private Border? _meterPeak;
    private Border? _meterPeakStereo;
    private Track? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _meterPeak = GetTemplateChild("MeterPeak") as Border;
        _meterPeakStereo = GetTemplateChild("MeterPeakStereo") as Border;
        _track = GetTemplateChild("PART_Track") as Track;
        UpdateMeterPeakWidths();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateMeterPeakWidths();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMeterPeakWidths();
    }

    private static void OnPeakValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VolumeSlider)d).UpdateMeterPeakWidths();

    private void UpdateMeterPeakWidths()
    {
        if (_meterPeak == null && _meterPeakStereo == null) return;

        // Subtract the thumb width so at peak=1 the overlay's flat-extent matches the thumb's left edge.
        // Track positions the thumb so its center sits at the value; the thumb's left edge is at
        // valueRatio * (ActualWidth - thumb.ActualWidth). The +radius nudge below pushes the rounded
        // cap's apex to that position so the meter doesn't appear one radius short of the thumb.
        double thumbWidth = _track?.Thumb?.ActualWidth ?? 0;
        double available = ActualWidth - thumbWidth;
        if (available <= 0)
        {
            if (_meterPeak != null) _meterPeak.Width = 0;
            if (_meterPeakStereo != null) _meterPeakStereo.Width = 0;
            return;
        }

        double range = Maximum - Minimum;
        double valueRatio = range > 0 ? (Value - Minimum) / range : 0;
        if (valueRatio < 0) valueRatio = 0;
        else if (valueRatio > 1) valueRatio = 1;

        // Nudge the bar's right edge by the corner radius so the rounded cap's apex sits where a
        // flat-cap bar's edge would land, instead of one radius short. Scaled by peak so the nudge
        // fades to 0 at silence and the bar still collapses cleanly to width 0.
        if (_meterPeak != null)
        {
            float peak = PeakValueMin;
            if (peak < 0f) peak = 0f;
            else if (peak > 1f) peak = 1f;
            double radius = _meterPeak.CornerRadius.TopLeft;
            _meterPeak.Width = available * valueRatio * peak + radius * peak;
        }

        if (_meterPeakStereo != null)
        {
            float peak = PeakValueMax;
            if (peak < 0f) peak = 0f;
            else if (peak > 1f) peak = 1f;
            double radius = _meterPeakStereo.CornerRadius.TopLeft;
            _meterPeakStereo.Width = available * valueRatio * peak + radius * peak;
        }
    }
}
