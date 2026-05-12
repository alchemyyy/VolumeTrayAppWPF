using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Click-track-jump + drag-with-capture behaviour for a vertical or horizontal <see cref="Slider"/>.
/// WPF's stock Slider handles thumb-on-cursor dragging fine; the case this behaviour covers is
/// a click on the visible track AWAY from the thumb. The native handling jumps the thumb once
/// but doesn't capture the mouse, so the thumb doesn't keep tracking the cursor through a drag.
/// This attached behaviour wires PreviewMouseLeftButtonDown / PreviewMouseMove / PreviewMouseLeftButtonUp
/// on the slider, captures the mouse on a track click, and updates the value from the cursor's
/// position relative to the slider's <see cref="Track"/> until the button releases.
/// Vertical orientation respects <see cref="Slider.IsDirectionReversed"/>.
/// Horizontal orientation maps left-to-right (Minimum -> Maximum) when unreversed.
/// Set <c>utils:SliderClickDragBehavior.Enable="True"</c> in XAML to opt in.
/// </summary>
public static class SliderClickDragBehavior
{
    /// <summary>Attached property: set to True to enable click-jump-and-drag on a Slider.</summary>
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(SliderClickDragBehavior),
        new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject d, bool value) => d.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);

    // Per-slider drag state. Keyed by the Slider itself; cleared on detach + on mouse-up.
    // Tiny in practice (one or two sliders mid-drag at most), but a dictionary keeps the behaviour
    // pure-attached without forcing consumers to hold a controller reference.
    private static readonly System.Collections.Generic.Dictionary<Slider, bool> _dragging = new();

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;

        bool enabled = (bool)e.NewValue;
        if (enabled)
        {
            slider.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            slider.PreviewMouseMove += OnPreviewMouseMove;
            slider.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        }
        else
        {
            slider.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            slider.PreviewMouseMove -= OnPreviewMouseMove;
            slider.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            _dragging.Remove(slider);
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        Track? track = FindVisualChild<Track>(slider);
        if (track?.Thumb == null) return;

        // Let the native thumb-drag take over when the click landed on the thumb itself.
        Rect thumbBounds = new(
            track.Thumb.TranslatePoint(new Point(0, 0), slider),
            new Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));
        if (thumbBounds.Contains(e.GetPosition(slider))) return;

        _dragging[slider] = true;
        slider.CaptureMouse();
        UpdateValueFromMousePosition(slider, track, e.GetPosition(slider));
        e.Handled = true;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (!_dragging.TryGetValue(slider, out bool active) || !active) return;

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            Track? track = FindVisualChild<Track>(slider);
            if (track != null) UpdateValueFromMousePosition(slider, track, e.GetPosition(slider));
        }
        else
            StopDragging(slider);
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (!_dragging.TryGetValue(slider, out bool active) || !active) return;
        StopDragging(slider);
    }

    private static void StopDragging(Slider slider)
    {
        _dragging.Remove(slider);
        slider.ReleaseMouseCapture();
    }

    /// <summary>
    /// Drives the slider value from a cursor position relative to the slider. Maps the usable
    /// track (excluding half a thumb at each end since the thumb's center can't extend past the
    /// track edges) to the slider's [Minimum, Maximum] range, flipping for IsDirectionReversed.
    /// Vertical orientation is the dominant case; horizontal sliders use the X axis instead.
    /// </summary>
    private static void UpdateValueFromMousePosition(Slider slider, Track track, Point position)
    {
        bool isVertical = slider.Orientation == Orientation.Vertical;
        double thumbExtent = isVertical
            ? (track.Thumb?.ActualHeight ?? 0)
            : (track.Thumb?.ActualWidth ?? 0);
        double sliderExtent = isVertical ? slider.ActualHeight : slider.ActualWidth;

        double trackStart = thumbExtent / 2;
        double trackEnd = sliderExtent - thumbExtent / 2;
        double trackLength = trackEnd - trackStart;
        if (trackLength <= 0) return;

        double cursorAlongTrack = isVertical ? position.Y : position.X;
        double normalized = Math.Clamp((cursorAlongTrack - trackStart) / trackLength, 0, 1);
        double range = slider.Maximum - slider.Minimum;

        // Vertical default: top = Maximum, bottom = Minimum (IsDirectionReversed=false).
        // Horizontal default: left = Minimum, right = Maximum (IsDirectionReversed=false).
        // IsDirectionReversed flips the mapping on either axis.
        double newValue;
        if (isVertical)
            newValue = slider.IsDirectionReversed
                ? slider.Minimum + normalized * range
                : slider.Maximum - normalized * range;
        else
            newValue = slider.IsDirectionReversed
                ? slider.Maximum - normalized * range
                : slider.Minimum + normalized * range;

        slider.Value = newValue;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;

            T? result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
}
