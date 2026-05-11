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

/// <summary>
/// Low-level shell notification icon implementation using Win32 APIs.
/// Pure interop wrapper - no business logic or throttling.
/// </summary>
internal sealed class ShellNotifyIcon : IDisposable
{
    public event Action? LeftMouseDown;
    public event Action? LeftClick;
    public event Action? LeftDoubleClick;
    public event Action<Point>? RightClick;
    public event Action? RefreshNeeded;
    /// <summary>
    /// Raised when the shell is about to display the icon's tooltip (NIN_POPUPOPEN).
    /// Use to refresh tooltip text against live state right before it becomes visible.
    /// </summary>
    public event Action? TooltipPopup;
    /// <summary>
    /// Mouse-wheel rotation while the cursor is over the tray icon.
    /// Positive = scroll up.
    /// Delivered via Raw Input (WM_INPUT), only registered while the cursor is in the icon's bounds.
    /// </summary>
    public event Action<int>? Scrolled;

    private const int WM_CALLBACKMOUSEMSG = User32.WM_USER + 1024;

    // Persistent GUID for this icon - reduces flicker on updates.
    // Derived from AppIdentity.AppGuid so two apps forked from the same skeleton can't
    // collide on the same icon identity in the shell registry (which would cause NIM_ADD
    // to fail and cross-app NIM_DELETEs to yank the wrong icon).
    private static readonly Guid IconGuid = new(AppIdentity.AppGuid);

    private readonly Win32Window _window;
    private readonly DispatcherTimer _taskbarRecreateTimer;
    private bool _isCreated;
    private bool _isVisible;
    private bool _disposed;
    private string _tooltipText = string.Empty;
    private Icon? _currentIcon;
    private bool _isContextMenuOpen;

    // Tray-scroll bookkeeping.
    // _isListeningForInput tracks whether a RAWINPUT subscription is currently registered for the tray window;
    // flipped by IsCursorWithinNotifyIconBounds as the cursor enters and leaves the icon.
    private RECT _trayIconLocation;
    private bool _isListeningForInput;
    private bool _isScrollEnabled = true;

    /// <summary>
    /// When false, the tray icon:
    /// <list type="bullet">
    ///   <item>does not track hover</item>
    ///   <item>does not query its bounds</item>
    ///   <item>does not subscribe to raw mouse input</item>
    ///   <item>does not raise <see cref="Scrolled"/></item>
    /// </list>
    /// Setting to false also tears down any active RAWINPUT subscription immediately.
    /// </summary>
    public bool IsScrollEnabled
    {
        get => _isScrollEnabled;
        set
        {
            if (_isScrollEnabled == value) return;

            _isScrollEnabled = value;
            if (!value && _isListeningForInput)
            {
                _isListeningForInput = false;
                InputHelper.UnregisterForMouseInput();
            }
        }
    }

    // Prevent double-click issues on Windows 11.
    private bool _hasProcessedButtonUp;
    private bool HasProcessedButtonUp
    {
        get
        {
            bool hasProcessedButtonUp = _hasProcessedButtonUp;
            _hasProcessedButtonUp = false;
            return hasProcessedButtonUp;
        }
        set => _hasProcessedButtonUp = value;
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (value != _isVisible)
            {
                _isVisible = value;
                Update();
            }
        }
    }

    public ShellNotifyIcon()
    {
        _window = new Win32Window();
        _window.Initialize(WndProc);

        // Re-registers the icon after the taskbar restarts.
        _taskbarRecreateTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(TimeConstants.TaskbarRecreateCheckIntervalMs)
        };
        _taskbarRecreateTimer.Tick += OnTaskbarRecreateTimerTick;
    }

    public void SetIcon(Icon icon)
    {
        if (icon == _currentIcon) return;

        _currentIcon = icon;
        Update();
    }

    public void SetTooltip(string text)
    {
        if (text == _tooltipText) return;

        _tooltipText = text;
        Update();
    }

    private NOTIFYICONDATAW MakeData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uFlags = NotifyIconFlags.NIF_MESSAGE
                | NotifyIconFlags.NIF_ICON
                | NotifyIconFlags.NIF_TIP
                | NotifyIconFlags.NIF_SHOWTIP
                | NotifyIconFlags.NIF_GUID,
            uCallbackMessage = WM_CALLBACKMOUSEMSG,
            hIcon = _currentIcon?.Handle ?? IntPtr.Zero,
            szTip = _tooltipText.Length > 127 ? _tooltipText[..127] : _tooltipText,
            guidItem = IconGuid
        };
    }

    private void Update()
    {
        if (_disposed) return;

        NOTIFYICONDATAW data = MakeData();

        if (!_isVisible)
        {
            if (_isCreated)
            {
                Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_DELETE, ref data);
                _isCreated = false;
            }
            return;
        }

        // Fast path: shell still knows about us, just push the new data.
        if (_isCreated && Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_MODIFY, ref data)) return;

        // Recovery path. Reached when either:
        //   - we never registered (first call, or a previous add failed), or
        //   - NIM_MODIFY just failed because the shell silently dropped the icon (sleep/resume,
        //     display-mode change, shell hiccup - none of which raise WM_TASKBARCREATED).
        // The persistent IconGuid means a re-add will be refused with E_FAIL
        // while the shell still holds a stale (GUID, hWnd) binding,
        // so issue a best-effort NIM_DELETE to clear it first.
        bool wasCreated = _isCreated;
        if (wasCreated) WPFLog.Log("ShellNotifyIcon.Update: NIM_MODIFY failed, falling back to delete+add recovery");
        _ = Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_DELETE, ref data);
        _isCreated = false;

        if (Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_ADD, ref data))
        {
            _isCreated = true;
            data.uTimeoutOrVersion = Shell32.NOTIFYICON_VERSION_4;
            Shell32.Shell_NotifyIconW(Shell32.NotifyIconMessage.NIM_SETVERSION, ref data);
        }
        else
        {
            int lastError = Marshal.GetLastWin32Error();
            WPFLog.Log($"ShellNotifyIcon.Update: NIM_ADD failed after recovery (lastError=0x{lastError:X8}); icon will retry on next update");
        }
    }

    private void WndProc(Message msg)
    {
        if (msg.Msg == WM_CALLBACKMOUSEMSG)
            CallbackMsgWndProc(msg);
        else if (msg.Msg == Shell32.WM_TASKBARCREATED)
        {
            // Taskbar recreated (explorer.exe restarted) - re-register icon
            ScheduleTaskbarRecreate();
        }
        else if (msg.Msg == User32.WM_INPUT)
        {
            // Defensive: if scroll was disabled mid-flight,
            // drop the packet before the GetRawInputData round-trip.
            if (!_isScrollEnabled) return;

            // Raw input only arrives while subscribed (cursor over icon).
            // Re-check bounds on each packet
            // - the cursor may have left the icon between subscribe and now.
            if (InputHelper.ProcessMouseInputMessage(msg.LParam, out int wheelDelta) &&
                wheelDelta != 0 &&
                IsCursorWithinNotifyIconBounds(Cursor.Position))
                Scrolled?.Invoke(wheelDelta);
        }
    }

    private void CallbackMsgWndProc(Message msg)
    {
        short notificationCode = (short)msg.LParam;

        switch (notificationCode)
        {
            case User32.WM_LBUTTONDOWN:
                LeftMouseDown?.Invoke();
                break;

            case (short)Shell32.NotifyIconNotification.NIN_SELECT:
            case User32.WM_LBUTTONUP:
                // Prevent double invocation on Windows 11 (barely works).
                if (!HasProcessedButtonUp)
                {
                    HasProcessedButtonUp = true;
                    LeftClick?.Invoke();
                }
                break;

            case User32.WM_LBUTTONDBLCLK:
                LeftDoubleClick?.Invoke();
                break;

            case User32.WM_RBUTTONUP:
            case User32.WM_CONTEXTMENU:
                Point cursorPosition = new(
                    (short)msg.WParam.ToInt32(),
                    msg.WParam.ToInt32() >> 16);
                RightClick?.Invoke(cursorPosition);
                break;

            case User32.WM_MOUSEMOVE:
                OnNotifyIconMouseMove();
                break;

            case (short)Shell32.NotifyIconNotification.NIN_POPUPOPEN:
                TooltipPopup?.Invoke();
                break;
        }
    }

    private void OnNotifyIconMouseMove()
    {
        // When scroll is disabled,
        // skip the Shell_NotifyIconGetRect query, bounds tracking, and any subsequent RAWINPUT subscription
        // - effectively dormant.
        if (!_isScrollEnabled) return;

        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            guidItem = IconGuid,
        };

        // Shell_NotifyIconGetRect returns S_OK (0) on success; only then is the rect valid.
        if (Shell32.Shell_NotifyIconGetRect(ref id, out RECT location) == 0)
        {
            _trayIconLocation = location;
            IsCursorWithinNotifyIconBounds(Cursor.Position);
        }
        else
        {
            // Couldn't resolve bounds;
            // drop any active subscription so we don't keep listening with stale coordinates.
            _trayIconLocation = default;
            if (_isListeningForInput)
            {
                _isListeningForInput = false;
                InputHelper.UnregisterForMouseInput();
            }
        }
    }

    private bool IsCursorWithinNotifyIconBounds(System.Drawing.Point cursor)
    {
        bool inBounds = _trayIconLocation.Contains(cursor);
        if (inBounds && !_isListeningForInput)
        {
            _isListeningForInput = true;
            InputHelper.RegisterForMouseInput(_window.Handle);
        }
        else if (!inBounds && _isListeningForInput)
        {
            _isListeningForInput = false;
            InputHelper.UnregisterForMouseInput();
        }
        return inBounds;
    }

    private int _remainingTicks;

    private void ScheduleTaskbarRecreate()
    {
        _remainingTicks = 10;
        _taskbarRecreateTimer.Start();
        Update();
    }

    private void OnTaskbarRecreateTimerTick(object? sender, EventArgs e)
    {
        _remainingTicks--;
        if (_remainingTicks <= 0)
        {
            _taskbarRecreateTimer.Stop();
            RefreshNeeded?.Invoke();
        }
    }

    // Inset between the modern-placed menu and the work-area edges.
    // Matches BrightnessFlyout.PositionNearTray so the menu and the flyout share the same docked offset.
    private const double ModernMenuPadding = 8;

    /// <summary>
    /// Shows a context menu at the specified position.
    /// In <see cref="ContextMenuPosition.Classic"/> mode the menu opens at <paramref name="point"/>
    /// (physical screen pixels from the WM_RBUTTONUP packet);
    /// in <see cref="ContextMenuPosition.Modern"/> mode the cursor point is ignored,
    /// and the menu is anchored to the bottom-right of the primary work area, like the Win11 system flyouts.
    /// </summary>
    public void ShowContextMenu(ContextMenu contextMenu, Point point, ContextMenuPosition placement)
    {
        if (_isContextMenuOpen) return;

        _isContextMenuOpen = true;

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
            // For a standard bottom taskbar, the icon lives below the work area,
            // so the vertical clamp pins the menu's bottom to workArea.Bottom - padding
            // while the horizontal center moves the menu directly above the icon.
            // Side/top taskbars get true centering along whichever axis the icon's center is in-bounds.
            // Falls back to the bottom-right corner when the icon's bounds aren't resolvable
            // (e.g. in the hidden overflow flyout, or not yet placed by the shell).
            double horizontalOffset = workArea.Right - desiredMenuSize.Width - ModernMenuPadding;
            double verticalOffset = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
            if (TryGetTrayIconRectInDips(out Rect iconRect))
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
            double dpiScale = GetDpiScale();
            double cursorTopDips = point.Y / dpiScale;
            double minTopDips = workArea.Top + ModernMenuPadding;
            double maxTopDips = workArea.Bottom - desiredMenuSize.Height - ModernMenuPadding;
            if (maxTopDips < minTopDips) maxTopDips = minTopDips;
            contextMenu.HorizontalOffset = point.X / dpiScale;
            contextMenu.VerticalOffset = Math.Clamp(cursorTopDips, minTopDips, maxTopDips);
        }

        contextMenu.Opened += OnContextMenuOpened;
        contextMenu.Closed += OnContextMenuClosed;
        contextMenu.IsOpen = true;
    }

    /// <summary>
    /// Resolves the tray icon's screen rectangle and converts it from physical pixels to WPF DIPs.
    /// Returns false when the shell can't (or won't) report the bounds -
    /// typically when the icon is hidden in the overflow flyout, or hasn't been placed yet.
    /// Queried fresh rather than reusing <see cref="_trayIconLocation"/>:
    /// that field is only refreshed by mouse-move tracking and is dormant when scroll is disabled.
    /// </summary>
    private bool TryGetTrayIconRectInDips(out Rect rectDips)
    {
        NOTIFYICONIDENTIFIER id = new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _window.Handle,
            guidItem = IconGuid,
        };

        if (Shell32.Shell_NotifyIconGetRect(ref id, out RECT rect) == 0)
        {
            double dpiScale = GetDpiScale();
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
    private static double GetDpiScale()
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

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Opened -= OnContextMenuOpened;
            menu.Closed -= OnContextMenuClosed;
        }
        _isContextMenuOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        if (_isListeningForInput)
        {
            _isListeningForInput = false;
            InputHelper.UnregisterForMouseInput();
        }

        _taskbarRecreateTimer.Stop();
        IsVisible = false;
        _window.Dispose();
    }
}
