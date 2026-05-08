using System.Windows;
using Point = System.Windows.Point;

namespace VolumeTrayAppWPF.WPF.Utils;

/// <summary>
/// Per-gesture drag math shared by the flyout's undock-button drag and root-card drag handlers.
/// Holds the cursor grab offset, the press-time window position (for click-vs-drag thresholding),
/// the docked-corner snap target, and the running snap state.
/// Each gesture re-arms the helper at MouseDown via <see cref="BeginDrag"/>;
/// per-Move calls to <see cref="ComputeNatural"/> + <see cref="ApplyDragPosition"/> reposition the window
/// and update <see cref="IsCurrentlySnapped"/> so the MouseUp handler can commit without re-checking tolerance.
/// </summary>
internal sealed class WindowDragHelper(Window window)
{
    // Cursor's position relative to the window's top-left at press, in WPF DIPs.
    // Subtracted from per-Move cursor positions to reconstruct where the window would sit
    // if it strictly pinned the cursor to the grab offset.
    private Point _grabOffset;
    // Window position at press. Threshold reference for the click-vs-drag discriminator.
    // Equal to (cursorScreenAtPress - grabOffset).
    private double _startLeft;
    private double _startTop;
    // Docked-corner target captured at drag start so the snap-back tolerance check stays stable
    // even if the working area shifts mid-gesture or the window crosses a DPI boundary.
    private double _dockedLeft;
    private double _dockedTop;
    // Snap zone width, in DIPs. Caller computes this as a fraction of the working area's smaller dimension
    // so the snap feels equally generous on a 1080p laptop, a 4K desktop, or an ultrawide.
    private double _snapTolerance;

    /// <summary>
    /// True while the natural cursor-following position is inside the dock snap zone,
    /// i.e. while the window is parked at the docked corner.
    /// Tracks across Move events so a release falls through to "redock" or "save undocked position"
    /// without re-checking. Important for a release right at the snap boundary,
    /// which should commit to whatever the user just saw a frame earlier.
    /// </summary>
    public bool IsCurrentlySnapped { get; private set; }

    /// <summary>
    /// Re-arms the helper at MouseDown.
    /// <paramref name="cursorInWindow"/> is the cursor relative to the window's top-left in DIPs
    /// (typically <c>e.GetPosition(window)</c>).
    /// <paramref name="dockedLeft"/>/<paramref name="dockedTop"/> are the docked-corner target;
    /// the caller is responsible for computing them since they depend on the window's docking strategy.
    /// Seeds <see cref="IsCurrentlySnapped"/> from the window's current position so a no-motion release
    /// at the docked corner still resolves to "redock".
    /// </summary>
    public void BeginDrag(Point cursorInWindow, double dockedLeft, double dockedTop, double snapTolerance)
    {
        _grabOffset = cursorInWindow;
        _startLeft = window.Left;
        _startTop = window.Top;
        _dockedLeft = dockedLeft;
        _dockedTop = dockedTop;
        _snapTolerance = snapTolerance;
        IsCurrentlySnapped = IsWithinSnapTolerance(window.Left, window.Top);
    }

    /// <summary>
    /// Where the window would sit if it strictly pinned the cursor to the press-time grab offset.
    /// Reconstructed from current Left + GetPosition (cursor screen pos in DIPs),
    /// so it's correct whether the window is currently at its natural spot
    /// or parked at the docked corner from a prior snap.
    /// </summary>
    public (double NaturalX, double NaturalY) ComputeNatural(Point cursorInWindow)
    {
        double naturalX = window.Left + cursorInWindow.X - _grabOffset.X;
        double naturalY = window.Top + cursorInWindow.Y - _grabOffset.Y;
        return (naturalX, naturalY);
    }

    /// <summary>
    /// Click-vs-drag discriminator. Compares total cursor screen motion against the threshold,
    /// using the press-time window position as the reference so the result is stable
    /// whether or not the window has been moved by snap logic.
    /// </summary>
    public bool ExceedsThreshold(double naturalX, double naturalY, double threshold)
    {
        double dx = naturalX - _startLeft;
        double dy = naturalY - _startTop;
        return dx * dx + dy * dy >= threshold * threshold;
    }

    /// <summary>
    /// Places the window at the docked corner when the natural-following position is inside the snap zone,
    /// otherwise places it at that natural position. Updates <see cref="IsCurrentlySnapped"/>
    /// so release handlers can commit the gesture without re-evaluating tolerance.
    /// </summary>
    public void ApplyDragPosition(double naturalX, double naturalY)
    {
        if (IsWithinSnapTolerance(naturalX, naturalY))
        {
            window.Left = _dockedLeft;
            window.Top = _dockedTop;
            IsCurrentlySnapped = true;
        }
        else
        {
            window.Left = naturalX;
            window.Top = naturalY;
            IsCurrentlySnapped = false;
        }
    }

    /// <summary>
    /// Docked corner captured at drag start. Useful for release handlers that need to pin the window
    /// back to dock without re-running the corner calculation.
    /// </summary>
    public double DockedLeft => _dockedLeft;

    public double DockedTop => _dockedTop;

    private bool IsWithinSnapTolerance(double left, double top) =>
        Math.Abs(left - _dockedLeft) <= _snapTolerance
        && Math.Abs(top - _dockedTop) <= _snapTolerance;
}
