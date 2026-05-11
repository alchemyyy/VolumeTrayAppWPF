using System.Windows;
using System.Windows.Controls;
using VolumeTrayAppWPF.Models;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Fixed-pitch wrap panel used by the flyout's icon-grid drawer. Defaults to a horizontally-oriented
/// WrapPanel with explicit ItemWidth / ItemHeight: items fill left-to-right within a row, rows fill
/// top-to-bottom. StackDirection swaps this to vertical-flow (items fill top-to-bottom within a
/// column, columns fill left-to-right) and / or reverses either axis (BottomTop, RightLeft).
/// Columns caps the primary-axis group: items-per-row in horizontal modes, items-per-column in
/// vertical ones. CenterLastRow shifts the trailing partial group to be centered along the
/// cross-axis; full groups stay anchored at the leading edge in either mode.
/// Auto resolution happens upstream; the panel only sees the four explicit directions.
/// </summary>
internal sealed class CenteringWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CenterLastRowProperty = DependencyProperty.Register(
        nameof(CenterLastRow), typeof(bool), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackDirectionProperty = DependencyProperty.Register(
        nameof(StackDirection), typeof(AppDrawerStackDirection), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(
            AppDrawerStackDirection.TopBottom,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CenterLastRow
    {
        get => (bool)GetValue(CenterLastRowProperty);
        set => SetValue(CenterLastRowProperty, value);
    }

    // When greater than zero, caps items along the primary axis (per row in horizontal modes, per
    // column in vertical modes) regardless of available cross-axis size. The panel's measured size
    // grows along the cross axis as more items are added. Zero (the default) falls back to
    // size-driven wrap on the cross axis.
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public AppDrawerStackDirection StackDirection
    {
        get => (AppDrawerStackDirection)GetValue(StackDirectionProperty);
        set => SetValue(StackDirectionProperty, value);
    }

    private bool IsVertical => StackDirection is AppDrawerStackDirection.LeftRight or AppDrawerStackDirection.RightLeft;
    private bool ReversePrimary => StackDirection == AppDrawerStackDirection.BottomTop;
    private bool ReverseCross => StackDirection == AppDrawerStackDirection.RightLeft;

    protected override Size MeasureOverride(Size availableSize)
    {
        double itemW = ItemWidth;
        double itemH = ItemHeight;
        if (double.IsNaN(itemW) || double.IsNaN(itemH) || itemW <= 0 || itemH <= 0)
        {
            return new Size(0, 0);
        }

        Size itemConstraint = new(itemW, itemH);
        for (int i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Measure(itemConstraint);

        int n = InternalChildren.Count;
        if (IsVertical)
        {
            // Per-column cap drives the vertical extent; columns accumulate horizontally as items are added.
            int perColumn = ResolvePrimary(availableSize.Height, itemH, n);
            int cols = n == 0 ? 0 : (int)Math.Ceiling((double)n / perColumn);
            return new Size(cols * itemW, perColumn * itemH);
        }
        else
        {
            int perRow = ResolvePrimary(availableSize.Width, itemW, n);
            int rows = n == 0 ? 0 : (int)Math.Ceiling((double)n / perRow);
            return new Size(perRow * itemW, rows * itemH);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double itemW = ItemWidth;
        double itemH = ItemHeight;
        if (double.IsNaN(itemW) || double.IsNaN(itemH) || itemW <= 0 || itemH <= 0)
        {
            return finalSize;
        }

        int n = InternalChildren.Count;
        if (n == 0) return finalSize;

        if (IsVertical)
        {
            int perColumn = ResolvePrimary(finalSize.Height, itemH, n);
            int cols = (int)Math.Ceiling((double)n / perColumn);
            // Trailing partial column is the one whose items spill past a full perColumn fill of preceding columns.
            int lastColStart = (n / perColumn) * perColumn;
            int lastColCount = n - lastColStart;
            bool centerLast = CenterLastRow && lastColCount > 0 && lastColCount < perColumn;
            double lastColOffset = centerLast ? (perColumn - lastColCount) * itemH / 2.0 : 0;

            for (int i = 0; i < n; i++)
            {
                int col = i / perColumn;
                int rowInCol = i % perColumn;

                int placedCol = ReverseCross ? (cols - 1 - col) : col;
                int placedRow = ReversePrimary ? (perColumn - 1 - rowInCol) : rowInCol;

                double x = placedCol * itemW;
                double y = placedRow * itemH + (i >= lastColStart ? lastColOffset : 0);
                InternalChildren[i].Arrange(new Rect(x, y, itemW, itemH));
            }
        }
        else
        {
            int perRow = ResolvePrimary(finalSize.Width, itemW, n);
            int rows = (int)Math.Ceiling((double)n / perRow);
            // Trailing partial row: same logic as before, just renamed for clarity now that vertical-flow exists.
            int lastRowStart = (n / perRow) * perRow;
            int lastRowCount = n - lastRowStart;
            bool centerLast = CenterLastRow && lastRowCount > 0 && lastRowCount < perRow;
            double lastRowOffset = centerLast ? (perRow - lastRowCount) * itemW / 2.0 : 0;

            for (int i = 0; i < n; i++)
            {
                int row = i / perRow;
                int colInRow = i % perRow;

                int placedRow = ReversePrimary ? (rows - 1 - row) : row;
                int placedCol = ReverseCross ? (perRow - 1 - colInRow) : colInRow;

                double x = placedCol * itemW + (i >= lastRowStart ? lastRowOffset : 0);
                double y = placedRow * itemH;
                InternalChildren[i].Arrange(new Rect(x, y, itemW, itemH));
            }
        }

        return finalSize;
    }

    // Resolves the per-row (horizontal flow) or per-column (vertical flow) cap for one layout pass.
    // An explicit Columns wins outright; otherwise falls back to size-driven wrap on the relevant axis.
    // Infinite or zero available size collapses to one strip of all children, mirroring WrapPanel's
    // behavior under the same condition.
    private int ResolvePrimary(double availableSize, double itemSize, int childCount)
    {
        int explicitColumns = Columns;
        if (explicitColumns > 0) return explicitColumns;
        if (double.IsInfinity(availableSize) || double.IsNaN(availableSize) || availableSize <= 0)
        {
            return Math.Max(1, childCount);
        }
        return Math.Max(1, (int)Math.Floor(availableSize / itemSize));
    }
}
