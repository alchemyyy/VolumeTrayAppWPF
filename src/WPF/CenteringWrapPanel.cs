using System.Windows;
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
/// vertical ones. CenterMode controls how a trailing partial group anchors along the cross axis:
/// Off (left / top), Centered (mid), CenteredSoftMax (left-anchored at the soft-max-row centered
/// position, switching to fully centered past CenterSoftMax items). Full groups stay anchored at
/// the leading edge in all modes.
/// Auto-direction resolution happens upstream; the panel only sees the four explicit directions.
/// </summary>
internal sealed class CenteringWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CenterModeProperty = DependencyProperty.Register(
        nameof(CenterMode), typeof(AppDrawerIconsCenterMode), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(
            AppDrawerIconsCenterMode.Off,
            FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CenterSoftMaxProperty = DependencyProperty.Register(
        nameof(CenterSoftMax), typeof(int), typeof(CenteringWrapPanel),
        new FrameworkPropertyMetadata(
            AppSettings.AppDrawerIconsCenterSoftMaxDefault,
            FrameworkPropertyMetadataOptions.AffectsArrange));

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

    public AppDrawerIconsCenterMode CenterMode
    {
        get => (AppDrawerIconsCenterMode)GetValue(CenterModeProperty);
        set => SetValue(CenterModeProperty, value);
    }

    public int CenterSoftMax
    {
        get => (int)GetValue(CenterSoftMaxProperty);
        set => SetValue(CenterSoftMaxProperty, value);
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
        if (double.IsNaN(itemW) || double.IsNaN(itemH) || itemW <= 0 || itemH <= 0) return new Size(0, 0);

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

        int perRow = ResolvePrimary(availableSize.Width, itemW, n);
        int rows = n == 0 ? 0 : (int)Math.Ceiling((double)n / perRow);
        return new Size(perRow * itemW, rows * itemH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double itemW = ItemWidth;
        double itemH = ItemHeight;
        if (double.IsNaN(itemW) || double.IsNaN(itemH) || itemW <= 0 || itemH <= 0) return finalSize;

        int n = InternalChildren.Count;
        if (n == 0) return finalSize;

        if (IsVertical)
        {
            int perColumn = ResolvePrimary(finalSize.Height, itemH, n);
            int cols = (int)Math.Ceiling((double)n / perColumn);
            int lastColStart = (n / perColumn) * perColumn;
            int lastColCount = n - lastColStart;
            double lastColOffset = ResolveTrailingGroupOffset(lastColCount, perColumn, itemH);

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
            int lastRowStart = (n / perRow) * perRow;
            int lastRowCount = n - lastRowStart;
            double lastRowOffset = ResolveTrailingGroupOffset(lastRowCount, perRow, itemW);

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

    // Resolves the cross-axis offset applied to every item in the trailing partial group, based on
    // the current CenterMode. Returns 0 for full groups (lastCount == primary) and empty groups
    // (lastCount == 0) under every mode -- only a partial trailing group ever shifts.
    private double ResolveTrailingGroupOffset(int lastCount, int primary, double itemSize)
    {
        if (lastCount <= 0 || lastCount >= primary) return 0;

        switch (CenterMode)
        {
            case AppDrawerIconsCenterMode.Centered:
                return (primary - lastCount) * itemSize / 2.0;

            case AppDrawerIconsCenterMode.CenteredSoftMax:
                int softMax = CenterSoftMax;
                if (softMax < 1) softMax = 1;
                if (softMax > primary) softMax = primary;
                // <= softMax: left-anchored at the position a centered softMax-icon row would occupy,
                // so adding icons up to softMax doesn't shift earlier items.
                // > softMax: behave like fully Centered.
                return lastCount <= softMax
                    ? (primary - softMax) * itemSize / 2.0
                    : (primary - lastCount) * itemSize / 2.0;

            default:
                return 0;
        }
    }

    // Resolves the per-row (horizontal flow) or per-column (vertical flow) cap for one layout pass.
    // An explicit Columns wins outright; otherwise falls back to size-driven wrap on the relevant axis.
    // Infinite or zero available size collapses to one strip of all children, mirroring WrapPanel's
    // behavior under the same condition.
    private int ResolvePrimary(double availableSize, double itemSize, int childCount)
    {
        int explicitColumns = Columns;
        if (explicitColumns > 0) return explicitColumns;
        if (double.IsInfinity(availableSize) || double.IsNaN(availableSize) || availableSize <= 0) return Math.Max(1, childCount);
        return Math.Max(1, (int)Math.Floor(availableSize / itemSize));
    }
}
