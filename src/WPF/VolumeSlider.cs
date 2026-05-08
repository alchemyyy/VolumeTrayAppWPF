using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Slider subclass that drives a peak-meter overlay sized by a separate <see cref="PeakValue"/>
/// dependency property. The control template (see VolumeFlyout.xaml) ships a named "MeterPeak"
/// FrameworkElement whose Width is recomputed here from PeakValue, the slider's Value, and the
/// thumb's actual width. PeakValue is in [0, 1] - the overlay never extends past the thumb's
/// left edge, so it stays visually contained inside the volume-progress fill no matter the volume.
/// Mirrors EarTrumpet's VolumeSlider, minus the stereo (PeakValue1/PeakValue2) split that the
/// VolumeTrayAppWPF endpoint meter doesn't surface.
/// </summary>
internal sealed class VolumeSlider : Slider
{
    public static readonly DependencyProperty PeakValueProperty = DependencyProperty.Register(
        nameof(PeakValue),
        typeof(float),
        typeof(VolumeSlider),
        new PropertyMetadata(0f, OnPeakValueChanged));

    public float PeakValue
    {
        get => (float)GetValue(PeakValueProperty);
        set => SetValue(PeakValueProperty, value);
    }

    private FrameworkElement? _meterPeak;
    private Track? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _meterPeak = GetTemplateChild("MeterPeak") as FrameworkElement;
        _track = GetTemplateChild("PART_Track") as Track;
        UpdateMeterPeakWidth();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateMeterPeakWidth();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMeterPeakWidth();
    }

    private static void OnPeakValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VolumeSlider)d).UpdateMeterPeakWidth();

    private void UpdateMeterPeakWidth()
    {
        if (_meterPeak == null) return;

        // Subtract the thumb width so the overlay's right edge tracks the thumb's left edge at peak=1.
        // Track positions the thumb so its center sits at the value; the thumb's left edge is at
        // valueRatio * (ActualWidth - thumb.ActualWidth), which is exactly the maximum the formula
        // below can produce (peak <= 1). So the overlay never overlaps the thumb visually.
        double thumbWidth = _track?.Thumb?.ActualWidth ?? 0;
        double available = ActualWidth - thumbWidth;
        if (available <= 0) { _meterPeak.Width = 0; return; }

        double range = Maximum - Minimum;
        double valueRatio = range > 0 ? (Value - Minimum) / range : 0;
        if (valueRatio < 0) valueRatio = 0;
        else if (valueRatio > 1) valueRatio = 1;

        float peak = PeakValue;
        if (peak < 0f) peak = 0f;
        else if (peak > 1f) peak = 1f;

        _meterPeak.Width = available * valueRatio * peak;
    }
}
