using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Models;
using Point = System.Windows.Point;

namespace VolumeTrayAppWPF.Interop;

// WPF context-menu placement extracted from ShellNotifyIcon.cs - it was a 190-line block of WPF
// popup positioning and DPI math that didn't belong in a "pure interop wrapper" file. Owns the
// ShowContextMenu helper plus the open/close hooks that scroll the menu to its bottom and pin
// foreground focus once it lights up.
internal static class ContextMenuPlacement
{
    // Inset between the modern-placed menu and the work-area edges. Matches BrightnessFlyout's
    // PositionNearTray so the menu and the flyout share the same docked offset.
    private const double ModernMenuPadding = 8;

    /// <summary>
    /// Shows a context menu at the specified position.
    /// In <see cref="ContextMenuPosition.Classic"/> mode the menu opens at <paramref name="point"/>
    /// (physical screen pixels from the WM_RBUTTONUP packet); in <see cref="ContextMenuPosition.Modern"/>
    /// mode the cursor point is ignored, and the menu is anchored to the bottom-right of the primary
    /// work area, like the Win11 system flyouts.
    /// </summary>
    public static void Show(
        ContextMenu contextMenu,
        Point point,
        ContextMenuPosition placement,
        IntPtr trayHwnd,
        Guid iconGuid,
        Action? onClosed = null)
    {
        contextMenu.StaysOpen = true;
        contextMenu.Placement = PlacementMode.AbsolutePoint;

        Rect workArea = SystemParameters.WorkArea;

        // Cap MaxHeight to the work area before measuring. This is the "handle it earlier" half:
        // an unbounded menu that's taller than the screen would trip WPF Popup's virtual-screen
        // auto-reposition, which slides the popup back inside virtualScreen and pushes the menu's
        // bottom (Settings, Exit) off the taskbar edge. With MaxHeight capped, the menu always fits,
        // WPF places it correctly in one pass, and items beyond the cap engage the default
        // ContextMenu template's MenuScrollViewer; OnContextMenuOpened scrolls to the bottom so the
        // critical items are visible by default and the overflow is hidden up top.
        contextMenu.MaxHeight = workArea.Height - 2 * ModernMenuPadding;

        // Pre-measure with the cap applied so DesiredSize reflects the rendered popup size.
        // The menu is fully built with all items added, so Measure produces a valid DesiredSize
        // without opening the popup first. SystemParameters.WorkArea is already in DIPs, matching WPF.
        contextMenu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        System.Windows.Size desiredMenuSize = contextMenu.DesiredSize;

        if (placement == ContextMenuPosition.Modern)
        {
            // Center the menu on the tray icon in both axes, clamped inside the work area.
            // For a standard bottom taskbar, the icon lives below the work area, so the vertical
            // clamp pins the menu's bottom to workArea.Bottom - padding while the horizontal center
            // moves the menu directly above the icon. Side/top taskbars get true centering along
            // whichever axis the icon's center is in-bounds. Falls back to the bottom-right corner
            // when the icon's bounds aren't resolvable (e.g. in the hidden overflow flyout, or not
            // yet placed by the shell).
            double horizontalOffset = workArea.Right - desiredMenuSize.Width - ModernMenuPadding;
            double verticalOffset = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
            if (TryGetTrayIconRectInDips(trayHwnd, iconGuid, out Rect iconRect))
            {
                double iconCenterX = (iconRect.Left + iconRect.Right) / 2.0;
                double iconCenterY = (iconRect.Top + iconRect.Bottom) / 2.0;
                double centeredLeft = iconCenterX - desiredMenuSize.Width / 2.0;
                double centeredTop = iconCenterY - desiredMenuSize.Height / 2.0;

                double minLeft = workArea.Left + ModernMenuPadding;
                double maxLeft = workArea.Right - desiredMenuSize.Width - ModernMenuPadding;
                if (maxLeft < minLeft) maxLeft = minLeft;
                horizontalOffset = Math.Clamp(centeredLeft, minLeft, maxLeft);

                double minTop = workArea.Top + ModernMenuPadding;
                double maxTop = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
                if (maxTop < minTop) maxTop = minTop;
                verticalOffset = Math.Clamp(centeredTop, minTop, maxTop);
            }
            contextMenu.HorizontalOffset = horizontalOffset;
            contextMenu.VerticalOffset = verticalOffset;
        }
        else
        {
            // Convert physical screen pixels to WPF DIPs and clamp the cursor-anchored top so the
            // menu's bottom never lands below workArea.Bottom - padding. The lower bound (top edge
            // clamped to workArea.Top + padding) holds too because MaxHeight keeps desiredMenuSize.Height
            // <= workArea.Height - 2*padding, which means maxTopDips >= minTopDips.
            double dpiScale = GetDPIScale();
            double cursorTopDips = point.Y / dpiScale;
            double minTopDips = workArea.Top + ModernMenuPadding;
            double maxTopDips = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
            if (maxTopDips < minTopDips) maxTopDips = minTopDips;
            contextMenu.HorizontalOffset = point.X / dpiScale;
            contextMenu.VerticalOffset = Math.Clamp(cursorTopDips, minTopDips, maxTopDips);
        }

        contextMenu.Opened += OnContextMenuOpened;
        // Wrap the close callback so the caller can re-enable its is-context-menu-open guard
        // without taking a dependency on this file's event-handler shape.
        void CloseAndForward(object sender, RoutedEventArgs e)
        {
            OnContextMenuClosed(sender, e);
            contextMenu.Closed -= CloseAndForward;
            onClosed?.Invoke();
        }
        contextMenu.Closed += CloseAndForward;
        contextMenu.IsOpen = true;
    }

    /// <summary>
    /// Resolves the tray icon's screen rectangle and converts it from physical pixels to WPF DIPs.
    /// Returns false when the shell can't (or won't) report the bounds - typically when the icon
    /// is hidden in the overflow flyout, or hasn't been placed yet. Queried fresh rather than
    /// reusing a cached field: the tray-scroll tracking inside ShellNotifyIcon only refreshes its
    /// bounds on mouse-move, and is dormant when scroll is disabled.
    /// </summary>
    private static bool TryGetTrayIconRectInDips(IntPtr trayHwnd, Guid iconGuid, out Rect rectDips)
    {
        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = trayHwnd,
            guidItem = iconGuid,
        };

        if (Shell32.Shell_NotifyIconGetRect(ref id, out RECT rect) == 0)
        {
            double dpiScale = GetDPIScale();
            rectDips = new Rect(
                rect.Left / dpiScale,
                rect.Top / dpiScale,
                (rect.Right - rect.Left) / dpiScale,
                (rect.Bottom - rect.Top) / dpiScale);
            return true;
        }

        rectDips = default;
        return false;
    }

    /// <summary>
    /// Gets the current DPI scale factor (e.g., 1.0 for 100%, 1.25 for 125%, 1.5 for 150%).
    /// </summary>
    private static double GetDPIScale()
    {
        try
        {
            IntPtr hdc = User32.GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                int dpi = User32.GetDeviceCaps(hdc, User32.LOGPIXELSX);
                _ = User32.ReleaseDC(IntPtr.Zero, hdc);
                return dpi / 96.0;
            }
        }
        catch
        {
            // Fall through to default
        }
        return 1.0;
    }

    private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            // Take focus so menu works properly.
            if (PresentationSource.FromVisual(menu) is HwndSource source) User32.SetForegroundWindow(source.Handle);

            menu.Focus();
            menu.StaysOpen = false;

            // Disable exit animation for snappier feel.
            if (menu.Parent is Popup popup) popup.PopupAnimation = PopupAnimation.None;

            // Scroll the items ScrollViewer to the bottom so the critical footer items
            // (Settings, Exit) are visible by default when the list overflows MaxHeight.
            // Deferred to Loaded priority because ScrollableHeight is 0 until layout completes -
            // calling ScrollToBottom in Opened is a no-op against an unmeasured ScrollViewer.
            menu.Dispatcher.BeginInvoke(new Action(() =>
            {
                ScrollViewer? scrollViewer = FindFirstVisualDescendant<ScrollViewer>(menu);
                scrollViewer?.ScrollToBottom();
            }), DispatcherPriority.Loaded);
        }
    }

    private static T? FindFirstVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            T? deeper = FindFirstVisualDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Opened -= OnContextMenuOpened;
            menu.Closed -= OnContextMenuClosed;
        }
    }
}
