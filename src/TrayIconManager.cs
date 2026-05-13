using System.Windows.Controls;
using System.Windows.Threading;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.Utils;
using Point = System.Windows.Point;

namespace VolumeTrayAppWPF;

/// <summary>
/// Manages the tray-icon lifecycle for the host application.
/// Owns the underlying ShellNotifyIcon and surfaces its events to the host.
/// Throttles icon updates so a hot-path updater can call <see cref="Update"/> without flicker.
///
/// Skeleton design: this class deliberately does not know how to render an icon.
/// The host supplies a delegate that produces a fresh <see cref="Icon"/> + tooltip pair on demand,
/// either through <see cref="Update"/> (throttled) or <see cref="ShellNotifyIcon"/> via direct setters.
/// Subscribe to <see cref="Scrolled"/>, <see cref="LeftClick"/>, etc. to wire host actions.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    // Throttle key. AsyncThrottler is per-key but there is only one tray icon, so a fixed single key
    // is the right shape - any Update call coalesces with the prior queued one.
    private const string ThrottleKey = "tray";

    private readonly ShellNotifyIcon _shellIcon;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncThrottler<string> _updateThrottler;
    private Func<(Icon? icon, string tooltip)>? _getValues;
    private bool _disposed;

    /// <summary>
    /// Cooldown between icon updates in milliseconds.
    /// Defaults to <see cref="TimeConstants.TrayIconUpdateRateDefaultMs"/>.
    /// </summary>
    public int UpdateCooldownMs
    {
        get => _updateThrottler.CooldownMs;
        set => _updateThrottler.CooldownMs = value;
    }

    /// <summary>Raised when the tray icon receives left mouse down.</summary>
    public event Action? LeftMouseDown;

    /// <summary>Raised when the tray icon is left-clicked.</summary>
    public event Action? LeftClick;

    /// <summary>Raised when the tray icon is left double-clicked.</summary>
    public event Action? LeftDoubleClick;

    /// <summary>Raised when the tray icon is right-clicked, with the cursor position.</summary>
    public event Action<Point>? RightClick;

    /// <summary>Raised when the icon needs to be refreshed (e.g. taskbar restarted).</summary>
    public event Action? RefreshNeeded;

    /// <summary>
    /// Raised when the user scrolls the mouse wheel over the tray icon.
    /// Argument is the wheel delta (positive = scroll up; one notch = +/-120).
    /// </summary>
    public event Action<int>? Scrolled;

    /// <summary>
    /// Raised right before the shell shows the tooltip.
    /// Host can refresh tooltip text via <see cref="SetTooltip"/> here without going through <see cref="Update"/>.
    /// </summary>
    public event Action? TooltipPopup;

    /// <summary>
    /// Raised when the user clicks the body of a balloon notification raised via <see cref="ShowBalloon"/>.
    /// </summary>
    public event Action? BalloonClicked;

    /// <summary>Whether the tray icon is visible.</summary>
    public bool IsVisible
    {
        get => _shellIcon.IsVisible;
        set => _shellIcon.IsVisible = value;
    }

    /// <summary>
    /// Master switch for the scroll-over-tray-icon feature.
    /// When false, the icon performs no hover tracking, no bounds queries, and no raw input subscription.
    /// </summary>
    public bool IsScrollEnabled
    {
        get => _shellIcon.IsScrollEnabled;
        set => _shellIcon.IsScrollEnabled = value;
    }

    public TrayIconManager()
    {
        // Capture the construction-thread dispatcher so Update() can marshal off-thread callers
        // back onto the window-owning thread. Shell_NotifyIconW is thread-affine and the cooldown flags
        // are unsynchronized - both want a single owner.
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _shellIcon = new ShellNotifyIcon();
        _updateThrottler = new AsyncThrottler<string>(TimeConstants.TrayIconUpdateRateDefaultMs);

        _shellIcon.LeftMouseDown += () => LeftMouseDown?.Invoke();
        _shellIcon.LeftClick += () => LeftClick?.Invoke();
        _shellIcon.LeftDoubleClick += () => LeftDoubleClick?.Invoke();
        _shellIcon.RightClick += point => RightClick?.Invoke(point);
        _shellIcon.RefreshNeeded += () => RefreshNeeded?.Invoke();
        _shellIcon.Scrolled += delta => Scrolled?.Invoke(delta);
        _shellIcon.TooltipPopup += () => TooltipPopup?.Invoke();
        _shellIcon.BalloonClicked += () => BalloonClicked?.Invoke();
    }

    /// <summary>
    /// Pushes a balloon (toast) through the underlying tray icon. Marshals to the dispatcher because
    /// Shell_NotifyIconW is thread-affine to the window the icon was registered against.
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => ShowBalloon(title, message));
            return;
        }
        _shellIcon.ShowBalloon(title, message);
    }

    /// <summary>
    /// Sets the tray icon directly, bypassing the cooldown. Useful for one-shot updates and tooltips
    /// outside the throttled <see cref="Update"/> path.
    /// </summary>
    public void SetIcon(Icon icon) => _shellIcon.SetIcon(icon);

    /// <summary>
    /// Sets the tray-icon tooltip text directly. ShellNotifyIcon dedupes internally so repeated calls
    /// with the same text are cheap.
    /// </summary>
    public void SetTooltip(string tooltip) => _shellIcon.SetTooltip(tooltip);

    /// <summary>
    /// Throttled icon refresh. The producer delegate is called immediately when no payload is in flight,
    /// and again after the cooldown expires if at least one Update arrived during the cooldown window.
    /// Safe to call from any thread; off-thread calls are marshaled onto the dispatcher so Shell_NotifyIconW
    /// stays single-owner.
    /// </summary>
    public void Update(Func<(Icon? icon, string tooltip)> getValues)
    {
        _getValues = getValues;
        // Schedule on the throttler. The payload marshals back to the dispatcher because Shell_NotifyIconW
        // and System.Drawing.Icon must touch the UI thread.
        _ = _updateThrottler.RunAsync(ThrottleKey, _ =>
        {
            if (_dispatcher.HasShutdownStarted) return Task.CompletedTask;
            if (_dispatcher.CheckAccess())
            {
                ApplyUpdate();
                return Task.CompletedTask;
            }
            return _dispatcher.InvokeAsync(ApplyUpdate).Task;
        });
    }

    /// <summary>
    /// Shows a context menu at the specified position.
    /// In <see cref="ContextMenuPosition.Classic"/> the menu opens at <paramref name="position"/>;
    /// in <see cref="ContextMenuPosition.Modern"/> the position is ignored and the menu docks
    /// to the bottom-right of the work area.
    /// </summary>
    public void ShowContextMenu(ContextMenu menu, Point position, ContextMenuPosition placement) =>
        _shellIcon.ShowContextMenu(menu, position, placement);

    private void ApplyUpdate()
    {
        Func<(Icon? icon, string tooltip)>? getter = _getValues;
        if (getter == null) return;

        (Icon? icon, string tooltip) = getter();
        if (icon != null) _shellIcon.SetIcon(icon);
        _shellIcon.SetTooltip(tooltip);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Safe.Dispose(_updateThrottler);
        Safe.Dispose(_shellIcon);
    }
}
