using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VolumeTrayAppWPF.Audio;
using MediaBrush = System.Windows.Media.Brush;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Slider subclass that drives a render-only peak-meter overlay from a coalesced
/// <see cref="MeterPeakValues"/> dependency property. The template owns one fixed-size
/// <see cref="PeakMeterOverlay"/> child; meter movement invalidates only that visual instead of
/// changing layout-affecting Border.Width values every frame. Peak values are in [0, 1]; at
/// peak=1 the bar's right edge extends one corner-radius past the thumb's left edge so the rounded
/// cap's apex aligns with it.
/// Rate-limiting (the user-facing MeterPeakChangeCeiling) lives in MeterLerp's render-tick, not
/// here - a downstream rate limiter would only advance when PropertyChanged fired and would get
/// stuck arbitrarily far from target once the lerp converged.
/// </summary>
internal sealed class VolumeSlider : Slider
{
    public static readonly DependencyProperty PeakValuesProperty = DependencyProperty.Register(
        nameof(PeakValues),
        typeof(MeterPeakValues),
        typeof(VolumeSlider),
        new PropertyMetadata(MeterPeakValues.Zero, OnPeakValuesChanged));

    public MeterPeakValues PeakValues
    {
        get => (MeterPeakValues)GetValue(PeakValuesProperty);
        set => SetValue(PeakValuesProperty, value);
    }

    private PeakMeterOverlay? _meterPeakOverlay;
    private Track? _track;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _meterPeakOverlay = GetTemplateChild("MeterPeakOverlay") as PeakMeterOverlay;
        _track = GetTemplateChild("PART_Track") as Track;

        UpdateMeterGeometry();
        UpdateMeterPeaks();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateMeterGeometry();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateMeterGeometry();
    }

    private static void OnPeakValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        VolumeSlider slider = (VolumeSlider)d;
        slider.UpdateMeterGeometry();
        slider.UpdateMeterPeaks();
    }

    private void UpdateMeterGeometry()
    {
        if (_meterPeakOverlay == null) return;

        // Subtract the thumb width so at peak=1 the overlay's flat-extent matches the thumb's left edge.
        // Track positions the thumb so its center sits at the value; the thumb's left edge is at
        // valueRatio * (ActualWidth - thumb.ActualWidth). PeakMeterOverlay adds the corner-radius
        // nudge when drawing so the rounded cap does not appear one radius short of the thumb.
        double thumbWidth = _track?.Thumb?.ActualWidth ?? 0;
        double available = ActualWidth - thumbWidth;
        if (available <= 0)
        {
            SetPeakExtent(0);
            return;
        }

        double range = Maximum - Minimum;
        double valueRatio = range > 0 ? (Value - Minimum) / range : 0;
        if (valueRatio < 0) valueRatio = 0;
        else if (valueRatio > 1) valueRatio = 1;

        SetPeakExtent(available * valueRatio);
    }

    private void SetPeakExtent(double peakExtent)
        => _meterPeakOverlay?.SetPeakExtent(peakExtent);

    private void UpdateMeterPeaks()
    {
        MeterPeakValues peaks = PeakValues;
        _meterPeakOverlay?.SetPeaks(peaks.Min, peaks.Max);
    }
}

internal sealed class PeakMeterOverlay : FrameworkElement
{
    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush),
        typeof(MediaBrush),
        typeof(PeakMeterOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StereoBrushProperty = DependencyProperty.Register(
        nameof(StereoBrush),
        typeof(MediaBrush),
        typeof(PeakMeterOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius),
        typeof(CornerRadius),
        typeof(PeakMeterOverlay),
        new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    private double _peakExtent;
    private float _peakMin;
    private float _peakMax;

    public MediaBrush? BaseBrush
    {
        get => (MediaBrush?)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public MediaBrush? StereoBrush
    {
        get => (MediaBrush?)GetValue(StereoBrushProperty);
        set => SetValue(StereoBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    internal void SetPeakExtent(double peakExtent)
    {
        if (_peakExtent == peakExtent) return;
        _peakExtent = peakExtent;
        InvalidateVisual();
    }

    internal void SetPeaks(float min, float max)
    {
        min = ClampPeak(min);
        max = ClampPeak(max);
        if (_peakMin == min && _peakMax == max) return;
        _peakMin = min;
        _peakMax = max;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double height = ActualHeight;
        if (height <= 0 || _peakExtent <= 0) return;

        double radius = CornerRadius.TopLeft;
        DrawPeak(drawingContext, StereoBrush, _peakMax, height, radius);
        DrawPeak(drawingContext, BaseBrush, _peakMin, height, radius);
    }

    private void DrawPeak(DrawingContext drawingContext, MediaBrush? brush, float peak, double height, double radius)
    {
        if (brush == null || peak <= 0f) return;

        // Nudge the bar's right edge by the corner radius so the rounded cap's apex sits where a
        // flat-cap bar's edge would land, instead of one radius short. Scaled by peak so the nudge
        // fades to 0 at silence and the bar still collapses cleanly to width 0.
        double width = _peakExtent * peak + radius * peak;
        if (width <= 0) return;

        drawingContext.DrawRoundedRectangle(
            brush,
            null,
            new Rect(0, 0, width, height),
            radius,
            radius);
    }

    private static float ClampPeak(float peak)
    {
        if (peak < 0f) return 0f;
        if (peak > 1f) return 1f;
        return peak;
    }
}
