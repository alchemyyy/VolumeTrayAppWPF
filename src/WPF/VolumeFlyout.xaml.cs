// Uncomment to pad the flyout cell list with 40 dummy cells cloned from the first real device.
// Verifies PositionNearTray / ClampTopForCriticalElement when the flyout overflows the work area.
// Flip the sibling toggle at the top of App.xaml.cs to test the tray context menu too.
// #define DEBUG_OVERFLOW_DUMMIES

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Localization;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Services;
using VolumeTrayAppWPF.WPF.Utils;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Tray flyout that lists every visible audio endpoint as a stacked cell, each cell containing the
/// device's per-app session sliders plus its own device-row volume slider.
/// DataContext is the window itself; the Cells collection is the bindable surface for the device list.
///
/// Lifecycle: constructed once at startup with a long-lived <see cref="AudioDeviceManager"/>.
/// The host calls <see cref="Show"/> on tray click; <see cref="OnDeactivated"/> hides on focus loss.
/// Peak metering is started on Show and stopped on Hide so the timer doesn't run idle in the background.
/// </summary>
internal partial class VolumeFlyout : Window, INotifyPropertyChanged
{
    // Click-vs-drag threshold for the undock button. Anything under this travels back to a click,
    // so a tiny shake doesn't accidentally undock-and-save-position when the user really meant to toggle.
    private const double DragThreshold = 4;

    // Snap zone width as a fraction of the working area's smaller dimension, so the snap-back
    // feels equally generous on a 1080p laptop, a 4K desktop, and an ultrawide.
    private const double SnapTolerancePercent = 0.02;

    // Single source of truth for the flyout's outer-edge inset. Used by PositionNearTray,
    // CaptureDockedPosition, and ApplyWorkAreaMaxHeight - hoisted to one site so a tweak lands in
    // every call. Padding (not a corner radius) - the work-area inset.
    private const double EdgePadding = 8;

    // Property names on AudioDevice that affect the cell list ordering. A change to any of these on
    // any device triggers a full rebuild so the StateGrouped sort picks up the new bucketing.
    private static readonly HashSet<string> OrderingPropertyNames = new(StringComparer.Ordinal)
    {
        nameof(AudioDevice.IsDefault),
        nameof(AudioDevice.IsDefaultCommunications),
        nameof(AudioDevice.State),
    };

    private readonly AudioDeviceManager _deviceManager;

    // Visible device cells in display order (top-to-bottom). Bound to the outer ItemsControl.
    private readonly ObservableCollection<VolumeFlyoutCell> _cells = [];

    // Our own AppID, computed once with the same lower-cased-image-path scheme AudioSession uses.
    // Per-app slider feedback (PlayForApp) routes through SoundPlayer / winmm, which registers an
    // audio session under our PID; without this filter, each cell would show its own slider
    // transiently while sound is playing. Null means we couldn't resolve our image path - fall back
    // to no filtering rather than guessing.
    private readonly string? _ownAppID;

    // Devices we have a PropertyChanged subscription on. Kept separately so add / remove
    // notifications from AudioDeviceManager.Devices can sync subscriptions without scanning twice.
    private readonly HashSet<AudioDevice> _subscribedDevices = new();

    // Cell wrappers indexed by device for incremental rebuild without disposing the wrapper on every
    // ordering change. Pruning happens when the device drops out of the visible list.
    private readonly Dictionary<AudioDevice, VolumeFlyoutCell> _cellsByDevice = new();

    // Shared CollectionChanged delegate hooked onto _cells and each cell's VisibleGroups so that
    // slider add / remove mutations while the flyout is hidden trigger a deferred UpdateLayout.
    // Caching the delegate lets us add / remove with the same reference per cell.
    private readonly NotifyCollectionChangedEventHandler _onSliderListChanged;

    // Coalesces a burst of slider list mutations into a single UpdateLayout post when hidden.
    private bool _hiddenLayoutQueued;

    private HwndSource? _hwndSource;
    private readonly AppSettings? _appSettings;

    // Per-app / per-device volume feedback. Owns the wav template, PCM resampling, and SoundPlayer
    // lifetime; the flyout fires PlayForDevice / PlayForApp on user interactions and never touches
    // the underlying primitives directly.
    private readonly AppVolumeFeedbackPlayer _feedback;

    // Snapshot of the docked screen-space coordinate, refreshed on every Show / size change.
    // Stored so SizeChanged re-anchoring re-uses a stable value if WorkArea shifts mid-flight.
    private double _dockedLeft;
    private double _dockedTop;

    // Dock/undock state. When undocked, the window is at a user-chosen position and doesn't auto-hide
    // on focus loss. The cursor grab offset, press-time window position, docked-corner snap target,
    // and running snap state all live on the helper, which is re-armed at MouseDown by both gestures.
    private bool _isUndocked;
    private bool _undockButtonDragOccurred;
    private bool _isDraggingFromBackground;
    private readonly WindowDragHelper _dragHelper;

    /// <summary>The visible device cells in display order. One cell per visible audio endpoint.</summary>
    public ReadOnlyObservableCollection<VolumeFlyoutCell> Cells { get; }

    /// <summary>True when at least one cell is visible. Drives the empty-state overlay.</summary>
    public bool HasCells => _cells.Count > 0;

    /// <summary>
    /// True while the flyout is in user-positioned (undocked) mode.
    /// XAML triggers on the undock button's glyph and tooltip bind to this property.
    /// </summary>
    public bool IsUndocked
    {
        get => _isUndocked;
        private set
        {
            if (_isUndocked == value) return;
            _isUndocked = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Drives the undock-button visibility from settings. Toggling this off while the flyout is
    /// undocked force-redocks at the next OnSettingsChanged tick so the user is never stranded
    /// with a free-floating window and no button to redock it.
    /// </summary>
    public bool AllowFlyoutUndock => _appSettings?.AllowFlyoutUndock ?? true;

    // Drives DockPanel.Dock for the title-bar row. The cells Grid is the DockPanel's last child so
    // it always fills the remaining space, regardless of which edge this property docks to. XAML
    // style triggers also pivot on this property to flip the buttons' Margin between top-edge and
    // bottom-edge breathing room (see VolumeFlyout.xaml).
    public Dock FlyoutHeaderDock => _appSettings?.FlyoutHeaderAtBottom == true ? Dock.Bottom : Dock.Top;

    // Bound by the listen button's visibility trigger in DeviceRowTemplate. Defaults true so the
    // button stays visible when the settings instance hasn't been wired (test harness / early init).
    public bool ShowListenButtonInFlyout => _appSettings?.ShowListenButtonInFlyout ?? true;

    // Grid.Row index for the title + control-buttons band inside DeviceRowTemplate.
    // BelowSlider (default) keeps the band on Grid.Row=1 under the slider; AboveSlider swaps it to row 0.
    // DeviceSliderRowIndex is the complement, so the two bands always occupy distinct rows.
    public int DeviceTitleRowIndex =>
        (_appSettings?.FlyoutDeviceTitlePosition ?? FlyoutDeviceTitlePosition.BelowSlider)
            == FlyoutDeviceTitlePosition.AboveSlider ? 0 : 1;

    public int DeviceSliderRowIndex =>
        (_appSettings?.FlyoutDeviceTitlePosition ?? FlyoutDeviceTitlePosition.BelowSlider)
            == FlyoutDeviceTitlePosition.AboveSlider ? 1 : 0;

    // Drives the SessionRowTemplate triggers that flag actively-capturing apps inside a recording
    // device's drawer. Defaults to DimInactive so early-init paths (no settings yet) match the
    // shipped default rather than rendering as "no indicator".
    public CaptureActivityIndicator CaptureActivityIndicator =>
        _appSettings?.CaptureActivityIndicator ?? CaptureActivityIndicator.ActiveGlyph;

    // Recording-device drawer style. Bound by the cell template selector to pick the icon-grid
    // template variant over the slider-row variant for capture cells. Defaults to Icons so the
    // shipped default applies when settings aren't wired (test harness / early init).
    public AppDrawerDisplayType RecordingAppDrawerDisplayType =>
        _appSettings?.RecordingAppDrawerDisplayType ?? AppDrawerDisplayType.Icons;

    // Icon-grid centering mode. Drives the CenteringWrapPanel.CenterMode binding in
    // AppsGridRowTemplate; AppDrawerIconsCenterSoftMax below feeds the soft-max-specific arm.
    public AppDrawerIconsCenterMode AppDrawerIconsCenterMode =>
        _appSettings?.AppDrawerIconsCenterMode ?? AppDrawerIconsCenterMode.Off;

    // Sanitised soft-max width for CenterMode = CenteredSoftMax. Bound to CenteringWrapPanel.CenterSoftMax.
    // Clamped to [1, 16] so a corrupt settings.xml can't push the value outside the spinner range;
    // the panel itself further clamps to the current primary-axis count at layout time.
    public int AppDrawerIconsCenterSoftMax
    {
        get
        {
            int n = _appSettings?.AppDrawerIconsCenterSoftMax ?? AppSettings.AppDrawerIconsCenterSoftMaxDefault;
            if (n < AppSettings.AppDrawerIconsCenterSoftMaxMin) return AppSettings.AppDrawerIconsCenterSoftMaxMin;
            if (n > AppSettings.AppDrawerIconsCenterSoftMaxMax) return AppSettings.AppDrawerIconsCenterSoftMaxMax;
            return n;
        }
    }

    // Base icon / glyph sizes routed through DPs so XAML Hot Reload edits to the AppIconImageSize /
    // AppIconGlyphSize resources flow through this property too. The Window's XAML root sets both
    // DPs via DynamicResource, so the resource dictionary owns the canonical value. The change
    // callback re-fires PropertyChanged on the scaled-grid versions so grid bindings re-evaluate
    // alongside the slider rows that bind the resources directly.
    public static readonly DependencyProperty BaseAppIconImageSizeProperty = DependencyProperty.Register(
        nameof(BaseAppIconImageSize), typeof(double), typeof(VolumeFlyout),
        new PropertyMetadata(22.0, OnBaseAppIconSizeChanged));

    public static readonly DependencyProperty BaseAppIconGlyphSizeProperty = DependencyProperty.Register(
        nameof(BaseAppIconGlyphSize), typeof(double), typeof(VolumeFlyout),
        new PropertyMetadata(20.0, OnBaseAppIconSizeChanged));

    public double BaseAppIconImageSize
    {
        get => (double)GetValue(BaseAppIconImageSizeProperty);
        set => SetValue(BaseAppIconImageSizeProperty, value);
    }

    public double BaseAppIconGlyphSize
    {
        get => (double)GetValue(BaseAppIconGlyphSizeProperty);
        set => SetValue(BaseAppIconGlyphSizeProperty, value);
    }

    private static void OnBaseAppIconSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        VolumeFlyout self = (VolumeFlyout)d;
        self.OnPropertyChanged(nameof(AppDrawerIconImageSize));
        self.OnPropertyChanged(nameof(AppDrawerIconGlyphSize));
        self.OnPropertyChanged(nameof(AppDrawerHoverPillSize));
    }

    // Per-icon pixel size for the grid drawer's Image. The base comes from BaseAppIconImageSize
    // (= the AppIconImageSize resource), multiplied by AppDrawerIconScalePercent so the percent
    // only takes effect in grid mode while slider mode binds the resource at 1:1.
    public double AppDrawerIconImageSize =>
        BaseAppIconImageSize * ((_appSettings?.AppDrawerIconScalePercent ?? 100) / 100.0);

    // Glyph FontSize for the fallback and mute-hover textblocks in the icon cell, scaled by the
    // same percent so the placeholder reads as a peer to a real icon at the chosen size.
    public double AppDrawerIconGlyphSize =>
        BaseAppIconGlyphSize * ((_appSettings?.AppDrawerIconScalePercent ?? 100) / 100.0);

    // Side length of the visible hover Border in GridIconTemplate. Icon size plus a 2-px ring,
    // clamped to the GridSlotSize resource so a high AppDrawerIconScalePercent never bleeds into
    // adjacent cells. Sits inside the 36x36 AppIconRoot Grid that owns the hit-test, so shrinking
    // this leaves the click region and grid pitch alone.
    public double AppDrawerHoverPillSize
    {
        get
        {
            double slot = (double)FindResource("GridSlotSize");
            double raw = AppDrawerIconImageSize + 2;
            return raw > slot ? slot : raw;
        }
    }

    // Sanitised column count for the icon grid. Bound to CenteringWrapPanel.Columns so the panel
    // wraps at exactly this many cells per row and sizes itself from its own ItemWidth. Clamped to
    // [1, 16] so a corrupt settings.xml can't collapse the grid or blow it past the screen.
    // In vertical stack-direction modes the same value is reinterpreted as items-per-column by the
    // panel itself; the Window only sanitises the user input.
    public int AppDrawerIconsPerRow
    {
        get
        {
            int n = _appSettings?.AppDrawerIconsPerRow ?? AppSettings.AppDrawerIconsPerRowDefault;
            if (n < AppSettings.AppDrawerIconsPerRowMin) return AppSettings.AppDrawerIconsPerRowMin;
            if (n > AppSettings.AppDrawerIconsPerRowMax) return AppSettings.AppDrawerIconsPerRowMax;
            return n;
        }
    }

    /// <summary>
    /// Resolved icon-grid stack direction. Auto picks BottomTop when apps sit above the device row
    /// (the bottom-most app abuts the device) and TopBottom when apps sit below. Bound to
    /// CenteringWrapPanel.StackDirection in AppsGridRowTemplate.
    /// </summary>
    public AppDrawerStackDirection AppDrawerStackDirection
    {
        get
        {
            AppDrawerStackDirection raw = _appSettings?.AppDrawerStackDirection ?? AppDrawerStackDirection.Auto;
            if (raw != AppDrawerStackDirection.Auto) return raw;

            FlyoutDeviceLayoutStyle layout = _appSettings?.FlyoutDeviceLayout ?? FlyoutDeviceLayoutStyle.AppsAboveDevice;
            return layout == FlyoutDeviceLayoutStyle.AppsAboveDevice
                ? AppDrawerStackDirection.BottomTop
                : AppDrawerStackDirection.TopBottom;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the flyout finishes its hide cycle so the host can toggle scroll bindings, etc.</summary>
    public event Action? FlyoutDeactivated;

    /// <summary>Raised when the user clicks the footer Settings button. Host opens the SettingsWindow.</summary>
    public event Action? SettingsRequested;

    public VolumeFlyout(AudioDeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        _appSettings = AppServices.Settings;
        Cells = new ReadOnlyObservableCollection<VolumeFlyoutCell>(_cells);

        // Resolve our own AppID once. Matches AudioSession's image-path-lower-invariant scheme
        // so an exact string compare in the cell filter suffices.
        string? ownPath = ProcessHelper.GetProcessImagePath((uint)Environment.ProcessId);
        _ownAppID = string.IsNullOrEmpty(ownPath) ? null : ownPath.ToLowerInvariant();

        _onSliderListChanged = (_, _) => QueueLayoutWhileHidden();
        ((INotifyCollectionChanged)_cells).CollectionChanged += _onSliderListChanged;

        InitializeComponent();
        DataContext = this;

        // Bind the base icon-size DPs to the root resources via DynamicResource so XAML Hot Reload
        // edits to AppIconImageSize / AppIconGlyphSize update both the slider rows (which bind the
        // resources directly) and the grid drawer (which goes through the scaled properties below).
        // Has to live in code-behind rather than as a XAML root attribute because the XAML compiler
        // resolves Window attributes against the literal Window type, not the partial subclass.
        SetResourceReference(BaseAppIconImageSizeProperty, "AppIconImageSize");
        SetResourceReference(BaseAppIconGlyphSizeProperty, "AppIconGlyphSize");

        _dragHelper = new WindowDragHelper(this);
        _feedback = new AppVolumeFeedbackPlayer(Dispatcher, _appSettings);

        IsUndocked = _appSettings is
        {
            FlyoutUndocked: true,
            FlyoutHasSavedPosition: true,
            AllowFlyoutUndock: true,
            RestoreFlyoutUndockedOnStartup: true
        };

        ((INotifyCollectionChanged)_deviceManager.Devices).CollectionChanged += OnDevicesCollectionChanged;
        SyncDeviceSubscriptions();
        RebuildCellList();

        if (_appSettings != null) _appSettings.Changed += OnAppSettingsChanged;

        SourceInitialized += OnFlyoutSourceInitialized;
        Closed += OnFlyoutClosed;
    }

    /// <summary>
    /// Settings change handler. Pulls the flyout back to docked when AllowFlyoutUndock flips off
    /// mid-session, refires INPC on every computed projection, and rebuilds the cell list so any
    /// sort / visibility setting flips into the visuals immediately.
    /// </summary>
    private void OnAppSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isUndocked && _appSettings?.AllowFlyoutUndock == false) Redock();
            OnPropertyChanged(nameof(AllowFlyoutUndock));
            OnPropertyChanged(nameof(FlyoutHeaderDock));
            OnPropertyChanged(nameof(ShowListenButtonInFlyout));
            OnPropertyChanged(nameof(DeviceTitleRowIndex));
            OnPropertyChanged(nameof(DeviceSliderRowIndex));
            OnPropertyChanged(nameof(CaptureActivityIndicator));
            OnPropertyChanged(nameof(RecordingAppDrawerDisplayType));
            OnPropertyChanged(nameof(AppDrawerIconsCenterMode));
            OnPropertyChanged(nameof(AppDrawerIconsCenterSoftMax));
            OnPropertyChanged(nameof(AppDrawerIconImageSize));
            OnPropertyChanged(nameof(AppDrawerIconGlyphSize));
            OnPropertyChanged(nameof(AppDrawerHoverPillSize));
            OnPropertyChanged(nameof(AppDrawerIconsPerRow));
            OnPropertyChanged(nameof(AppDrawerStackDirection));

            RebuildCellList();

            // Header dock flip changes the SettingsButton's window-space Y offset but not the
            // window height, so OnRenderSizeChanged will not re-clamp on its own. Force a layout
            // pass and re-anchor so ClampTopForCriticalElement sees the new offset.
            if (IsVisible)
            {
                UpdateLayout();
                PositionNearTray();
            }
        });
    }

    /// <summary>
    /// Force MA_ACTIVATE on the click that opens the flyout so the same click also reaches WPF input.
    /// Without this, the first slider drag right after a tray click can be eaten by the activation transition.
    /// </summary>
    private void OnFlyoutSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WindowProcHook);
    }

    private static IntPtr WindowProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != User32.WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(User32.MA_ACTIVATE);
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WindowProcHook);
            _hwndSource = null;
        }
        ((INotifyCollectionChanged)_deviceManager.Devices).CollectionChanged -= OnDevicesCollectionChanged;
        foreach (AudioDevice d in _subscribedDevices) d.PropertyChanged -= OnDevicePropertyChanged;
        _subscribedDevices.Clear();
        if (_appSettings != null) _appSettings.Changed -= OnAppSettingsChanged;

        ((INotifyCollectionChanged)_cells).CollectionChanged -= _onSliderListChanged;
        foreach (VolumeFlyoutCell cell in _cellsByDevice.Values)
        {
            ((INotifyCollectionChanged)cell.VisibleGroups).CollectionChanged -= _onSliderListChanged;
            cell.Dispose();
        }
        _cellsByDevice.Clear();
        _cells.Clear();

        try { _feedback.Dispose(); }
        catch { /* shutdown best-effort */ }
    }

    /// <summary>
    /// Sync our per-device PropertyChanged subscription set against the manager's current device list.
    /// </summary>
    private void SyncDeviceSubscriptions()
    {
        AudioDevice[] live = _deviceManager.Devices.ToArray();
        HashSet<AudioDevice> liveSet = new(live);

        // Unsubscribe devices that left.
        foreach (AudioDevice d in _subscribedDevices.ToArray())
        {
            if (!liveSet.Contains(d))
            {
                d.PropertyChanged -= OnDevicePropertyChanged;
                _subscribedDevices.Remove(d);
            }
        }

        // Subscribe devices that are new since the last sync.
        for (int i = 0; i < live.Length; i++)
        {
            AudioDevice d = live[i];
            if (_subscribedDevices.Add(d)) d.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncDeviceSubscriptions();
        RebuildCellList();
        if (IsVisible) PositionNearTray();
    }

    /// <summary>
    /// Per-device PropertyChanged. Filtered to ordering-affecting properties so a volume drag or
    /// peak-meter tick doesn't churn the cell list. The rebuild is cheap (single pass of
    /// FlyoutDeviceOrdering.Build); the filter exists primarily to suppress a UI ripple on every
    /// peak-sample callback.
    /// </summary>
    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || !OrderingPropertyNames.Contains(e.PropertyName)) return;
        RebuildCellList();
    }

    /// <summary>
    /// Recomputes the visible cell list from the manager's device list and the current settings.
    /// Diff-based - existing cells are kept (preserving their internal state); only cells for
    /// removed-from-visible devices are disposed. Layout / drawer-type flips no longer force a
    /// full rebuild: the unified DataTemplate's DataTriggers restyle live cells in place.
    /// </summary>
    private void RebuildCellList()
    {
        if (_appSettings == null)
        {
            // Without settings, fall back to listing every render device in enumeration order.
            ApplyOrderedDevices(_deviceManager.Devices.Where(d => d.DataFlow == Audio.Interop.EDataFlow.eRender).ToList());
            return;
        }

        List<AudioDevice> ordered = FlyoutDeviceOrdering.Build(_deviceManager.Devices, _appSettings);
        ApplyOrderedDevices(ordered);
    }

    private void ApplyOrderedDevices(List<AudioDevice> ordered)
    {
#if DEBUG_OVERFLOW_DUMMIES
        // Drop the previous rebuild's dummy cells (anything not tracked by _cellsByDevice) so
        // re-rebuilds don't accumulate dummies on top of dummies.
        HashSet<VolumeFlyoutCell> realCells = new(_cellsByDevice.Values);
        for (int dummyIndex = _cells.Count - 1; dummyIndex >= 0; dummyIndex--)
            if (!realCells.Contains(_cells[dummyIndex])) _cells.RemoveAt(dummyIndex);
#endif

        // Step 1: prune cells whose device left the visible list.
        HashSet<AudioDevice> orderedSet = new(ordered);
        foreach (KeyValuePair<AudioDevice, VolumeFlyoutCell> kv in _cellsByDevice.ToArray())
        {
            if (!orderedSet.Contains(kv.Key))
            {
                ((INotifyCollectionChanged)kv.Value.VisibleGroups).CollectionChanged -= _onSliderListChanged;
                _cells.Remove(kv.Value);
                kv.Value.Dispose();
                _cellsByDevice.Remove(kv.Key);
            }
        }

        // Step 2: ensure each ordered device has a cell, and place cells in target order.
        for (int targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            AudioDevice device = ordered[targetIndex];
            if (!_cellsByDevice.TryGetValue(device, out VolumeFlyoutCell? cell))
            {
                cell = new VolumeFlyoutCell(device, _ownAppID);
                ((INotifyCollectionChanged)cell.VisibleGroups).CollectionChanged += _onSliderListChanged;
                _cellsByDevice[device] = cell;
            }

            int currentIndex = _cells.IndexOf(cell);
            if (currentIndex == targetIndex) continue;

            if (currentIndex < 0) _cells.Insert(targetIndex, cell);
            else _cells.Move(currentIndex, targetIndex);
        }

        // Step 3: stamp positional flags on every cell.
        for (int i = 0; i < _cells.Count; i++)
        {
            _cells[i].IsFirst = i == 0;
            _cells[i].IsLast = i == _cells.Count - 1;
        }

#if DEBUG_OVERFLOW_DUMMIES
        // Append 40 dummy cells reusing the first real device so the flyout grows past the screen.
        // The duplicates piggy-back the device's CollectionChanged subscription - cheap, leaks one
        // handler ref per dummy if the flag is toggled at runtime, fine for a debug test.
        if (_cells.Count > 0)
        {
            AudioDevice template = _cells[0].Device;
            for (int dummyIndex = 0; dummyIndex < 40; dummyIndex++)
                _cells.Add(new VolumeFlyoutCell(template, _ownAppID));
            for (int i = 0; i < _cells.Count; i++)
            {
                _cells[i].IsFirst = i == 0;
                _cells[i].IsLast = i == _cells.Count - 1;
            }
        }
#endif

        OnPropertyChanged(nameof(HasCells));
    }

    /// <summary>
    /// Positions the flyout. Anchors to the bottom-right of the working area when docked, or
    /// restores the user's saved undocked coordinates when undocked. The final Top is clamped
    /// through <see cref="ClampTopForCriticalElement"/> so the Settings button stays inside the
    /// work area even when the cell list grows taller than the screen. The clamp is applied at
    /// each call - the saved undocked Top is not mutated, so the user's chosen position is
    /// restored verbatim once the content shrinks back to fit.
    /// </summary>
    public void PositionNearTray()
    {
        Rect workArea = SystemParameters.WorkArea;
        _dockedLeft = workArea.Right - Width - EdgePadding;
        _dockedTop = workArea.Bottom - ActualHeight - EdgePadding;

        double targetLeft;
        double targetTop;
        if (_isUndocked && _appSettings?.FlyoutHasSavedPosition == true)
        {
            targetLeft = _appSettings.FlyoutLeft;
            targetTop = _appSettings.FlyoutTop;
        }
        else
        {
            targetLeft = _dockedLeft;
            targetTop = _dockedTop;
        }

        Left = targetLeft;
        Top = ClampTopForCriticalElement(targetTop, SettingsButton, workArea, EdgePadding);
    }

    /// <summary>
    /// Clamps a proposed window Top so the named critical child element stays inside the work area.
    /// Layout-agnostic in both directions - works whether the element sits near the top of the
    /// window (current header) or near the bottom (future layouts). When the constraints conflict
    /// (window taller than the work area), the upper bound wins so the critical element is kept
    /// visible at the top edge while the opposite side of the window spills off-screen.
    /// No-op until the element has been laid out at least once - the next render pass re-fires
    /// <see cref="OnRenderSizeChanged"/>, which re-anchors with a valid offset.
    /// </summary>
    private double ClampTopForCriticalElement(double proposedTop, FrameworkElement critical, Rect workArea, double padding)
    {
        if (critical == null || !critical.IsLoaded || critical.ActualHeight <= 0) return proposedTop;

        System.Windows.Point offset = critical.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
        double criticalHeight = critical.ActualHeight;

        // The element's screen-space top under the proposed window top is proposedTop + offset.Y.
        // Constraint: that top must sit inside [workArea.Top + padding, workArea.Bottom - height - padding].
        // Solved for proposedTop, the allowed band is:
        double minTopAllowed = workArea.Top + padding - offset.Y;
        double maxTopAllowed = workArea.Bottom - criticalHeight - padding - offset.Y;
        if (maxTopAllowed < minTopAllowed) maxTopAllowed = minTopAllowed;

        return Math.Clamp(proposedTop, minTopAllowed, maxTopAllowed);
    }

    private double CaptureDockedPosition()
    {
        Rect workArea = SystemParameters.WorkArea;
        _dockedLeft = workArea.Right - Width - EdgePadding;
        _dockedTop = workArea.Bottom - ActualHeight - EdgePadding;
        return Math.Min(workArea.Width, workArea.Height) * SnapTolerancePercent;
    }

    public void Redock()
    {
        IsUndocked = false;
        if (_appSettings != null)
        {
            _appSettings.FlyoutUndocked = false;
            _appSettings.Save();
        }
        PositionNearTray();
    }

    private void UndockToSavedPosition()
    {
        IsUndocked = true;
        if (_appSettings != null)
        {
            _appSettings.FlyoutUndocked = true;
            _appSettings.Save();
        }

        if (_appSettings?.FlyoutHasSavedPosition == true)
        {
            Left = _appSettings.FlyoutLeft;
            Top = _appSettings.FlyoutTop;
        }
    }

    private void SaveUndockedPosition()
    {
        if (_appSettings == null) return;

        _appSettings.FlyoutUndocked = true;
        _appSettings.FlyoutHasSavedPosition = true;
        _appSettings.FlyoutLeft = Left;
        _appSettings.FlyoutTop = Top;
        _appSettings.Save();
        IsUndocked = true;
    }

    /// <summary>Shows the flyout near the tray and starts peak metering.</summary>
    public new void Show()
    {
        ApplyWorkAreaMaxHeight();
        base.Show();
        UpdateLayout();
        PositionNearTray();
        Activate();
        Focus();
        _deviceManager.StartMetering();
        ScrollCellsToBottom();
    }

    /// <summary>
    /// Shows the flyout without taking foreground/keyboard focus from the caller. Used when
    /// SettingsWindow re-opens the flyout on its own activation.
    /// </summary>
    public void ShowWithoutActivating()
    {
        ApplyWorkAreaMaxHeight();

        bool previousShowActivated = ShowActivated;
        ShowActivated = false;
        try { base.Show(); }
        finally { ShowActivated = previousShowActivated; }

        UpdateLayout();
        PositionNearTray();
        _deviceManager.StartMetering();
        ScrollCellsToBottom();
    }

    /// <summary>
    /// Caps the flyout's height to the work area so an overflowing cell list scrolls inside the
    /// flyout rather than the window growing past the screen and pushing the header (Settings
    /// button) off the top edge. SizeToContent="Height" still auto-sizes when the cells fit;
    /// MaxHeight only takes effect when they don't.
    /// </summary>
    private void ApplyWorkAreaMaxHeight()
    {
        MaxHeight = SystemParameters.WorkArea.Height - 2 * EdgePadding;
    }

    /// <summary>
    /// Scrolls <see cref="CellsScrollViewer"/> to the bottom so the default device (which sits
    /// at the bottom of the cell list per FlyoutDeviceOrdering) is visible by default when the
    /// list overflows. Deferred to Loaded priority because ScrollableHeight is 0 until layout
    /// completes - an immediate call against an unmeasured ScrollViewer is a no-op.
    /// </summary>
    private void ScrollCellsToBottom()
    {
        Dispatcher.BeginInvoke(CellsScrollViewer.ScrollToBottom, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the flyout and stops peak metering.</summary>
    public new void Hide()
    {
        _deviceManager.StopMetering();
        base.Hide();
    }

    /// <summary>
    /// Forces a deferred layout pass while the flyout is hidden. Hooked from collection-change
    /// events on the cell list and each cell's visible-groups list, so a session arriving or
    /// leaving while closed walks Measure / Arrange immediately instead of piling up on the first
    /// frame of <see cref="Show"/>. Coalesced through one Background dispatcher post per burst so
    /// rapid-fire mutations only trigger a single UpdateLayout. Background priority lets binding
    /// and container generation flush before we measure. No-op while visible - WPF runs layout
    /// naturally then.
    /// </summary>
    private void QueueLayoutWhileHidden()
    {
        if (_hiddenLayoutQueued || IsVisible) return;
        _hiddenLayoutQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _hiddenLayoutQueued = false;
            if (IsVisible) return;
            UpdateLayout();
        }, DispatcherPriority.Background);
    }

    /// <summary>True when the OS-level foreground window is this flyout's HWND.</summary>
    public bool HasFocus()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && hwnd == User32.GetForegroundWindow();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Height changes are common as cells arrive / depart - re-anchor so the bottom edge stays pinned.
        if (IsVisible && sizeInfo.HeightChanged) PositionNearTray();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Undocked acts like a real window - stays open across focus changes. The user dismisses it
        // by redocking (button or tray click), at which point the docked path's auto-hide-on-deactivate
        // behavior resumes.
        if (_isUndocked) return;

        SettingsWindow? settings = null;
        foreach (Window window in System.Windows.Application.Current.Windows)
            if (window is SettingsWindow s) { settings = s; break; }

        if (settings == null)
        {
            HideAndNotify();
            return;
        }

        if (settings.HasFocus()) return;

        bool keep = false;
        EventHandler? onActivated = null;
        onActivated = (_, _) =>
        {
            settings.Activated -= onActivated;
            keep = true;
        };
        settings.Activated += onActivated;

        Dispatcher.BeginInvoke(() =>
        {
            settings.Activated -= onActivated;
            if (keep || settings.HasFocus()) return;

            HideAndNotify();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void HideAndNotify()
    {
        Hide();
        FlyoutDeactivated?.Invoke();
    }

    /// <summary>
    /// Mouse-up on a slider fires per-app / per-device volume feedback. The captured-drag side of
    /// the gesture lives in SliderClickDragBehavior; we only need to play the wav once the user
    /// finishes interacting (thumb drag or track click). immediate=true bypasses the trailing
    /// dwell that the wheel handler relies on - a drag release is a single discrete commit, so the
    /// ding should land on mouse-up rather than ~200ms later.
    /// Branches on DataContext type: the slider's context is the device wrapper for device sliders
    /// and the AudioAppGroup for session sliders.
    /// </summary>
    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (slider.DataContext is AudioDevice device) _feedback.PlayForDevice(device, immediate: true);
        else if (slider.DataContext is AudioAppGroup group) _feedback.PlayForApp(group.Volume, immediate: true);
    }

    /// <summary>
    /// Unified mouse-wheel handler for both device and per-app sliders. Branches on the slider's
    /// DataContext type to find the right scalar volume target. Step matches AppConstants.WheelVolumeStepPercent
    /// so the in-flyout gesture and the tray-icon wheel handler step identically.
    /// </summary>
    private void Slider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        double currentPercent;
        AudioDevice? device = fe.DataContext as AudioDevice;
        AudioAppGroup? group = fe.DataContext as AudioAppGroup;
        if (device != null) currentPercent = device.Volume * 100.0;
        else if (group != null) currentPercent = group.Volume * 100.0;
        else return;

        double step = e.Delta > 0 ? AppConstants.WheelVolumeStepPercent : -AppConstants.WheelVolumeStepPercent;
        double next = currentPercent + step;
        if (next < 0) next = 0;
        else if (next > 100) next = 100;

        bool changed = Math.Abs(next - currentPercent) > 0.001;
        float scalar = (float)(next / 100.0);

        if (device != null)
        {
            device.Volume = scalar;
            if (changed) _feedback.PlayForDevice(device);
        }
        else if (group != null)
        {
            group.Volume = scalar;
            if (changed) _feedback.PlayForApp(group.Volume);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Toggles mute on the device whose row hosts this button. The button's Tag holds the bound
    /// AudioDevice; binding eliminates the visual-tree walk the old handler needed.
    /// </summary>
    private void DeviceMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        device.IsMuted = !device.IsMuted;
    }

    /// <summary>
    /// Device-icon button click. Plain -> toggle enable/disable; Ctrl -> set as default and open
    /// classic device properties; Shift -> set as default communications.
    /// </summary>
    private void DeviceIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;

        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (shift) device.SetAsDefaultCommunications();
        else if (ctrl) device.SetEnabled(!device.IsActive);
        else device.SetAsDefault();
    }

    /// <summary>Footer Settings button. Hands off to the host; App.xaml.cs opens SettingsWindow.</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>
    /// Titlebar Sound-settings button. Opens the Windows surface picked by
    /// <see cref="AppSettings.SoundSettingsTarget"/> - mmsys.cpl (classic panel) by default,
    /// ms-settings:sound when the user has opted into the modern Settings app.
    /// </summary>
    private void SoundSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SoundSettingsTarget target = _appSettings?.SoundSettingsTarget ?? SoundSettingsTarget.LegacySoundPanel;
        DeviceShellLinks.OpenSoundSettings(target);
    }

    private void DeviceIcon_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        DeviceShellLinks.OpenDeviceProperties(device);
        e.Handled = true;
    }

    /// <summary>
    /// Exclusive-mode button click. Flips the "Allow applications to take exclusive control of this
    /// device" bit on the endpoint. The held-vs-not-held state is observed (not controlled) by this
    /// button - clicking it never wrests control from an app already streaming exclusively.
    /// </summary>
    private void ExclusiveModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        device.ToggleAllowExclusiveControl();
    }

    /// <summary>
    /// Equalizer APO button click. Plain click is the two-state toggle (Running -> uninstall;
    /// everything else -> install + enable). Ctrl+click opens the Configuration Editor scoped to
    /// this device. When the APO binary can't be found anywhere on the system, plain click routes
    /// to the not-available dialog; ctrl+click still tries the editor (no-op stub) so the gesture
    /// stays consistent across states.
    /// </summary>
    private void EqualizerAPOButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            EqualizerAPOMonitor.OpenConfigurationEditor(device);
            return;
        }

        if (device.EqualizerAPOState == EqualizerAPOState.NotAvailable)
        {
            ShowEqualizerAPONotAvailableDialog();
            return;
        }
        device.ToggleEqualizerAPO();
    }

    /// <summary>
    /// Right-click on the equalizer button. Opens the Configuration Editor scoped to this device,
    /// matching the ctrl+left-click gesture so power users have two equivalent paths to the editor.
    /// </summary>
    private void EqualizerAPOButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        EqualizerAPOMonitor.OpenConfigurationEditor(device);
        e.Handled = true;
    }

    /// <summary>
    /// Surfaces the "Equalizer APO not detected" message with two actions: download the latest x64
    /// installer in the user's browser, or open this app's settings so the user can point us at a
    /// custom install path. Implemented with a MessageBox for now - matches the existing dialog
    /// style elsewhere in the app and keeps the touch-up small.
    /// </summary>
    private void ShowEqualizerAPONotAvailableDialog()
    {
        string title = LocalizationManager.Instance["EqualizerAPO_NotAvailable_Title"];
        string body = LocalizationManager.Instance["EqualizerAPO_NotAvailable_Body"];
        string downloadBtn = LocalizationManager.Instance["EqualizerAPO_NotAvailable_DownloadButton"];

        // Simple two-choice prompt: Yes = open the installer page in the default browser; No = cancel.
        // A richer custom-dialog with a third "Open settings" button would mean introducing a new
        // window class; deferring that until the settings entry for a custom EAPO path actually exists.
        MessageBoxResult result = System.Windows.MessageBox.Show(
            this,
            $"{body}\n\n{downloadBtn}?",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                using System.Diagnostics.Process? _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = EqualizerAPOMonitor.LatestInstallerURL,
                        UseShellExecute = true,
                    });
            }
            catch (Exception ex) { WPFLog.Log($"VolumeFlyout.ShowEqualizerAPONotAvailableDialog: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Listen-to-this-device button click. Plain click toggles the enable bit and leaves the
    /// previously-configured target alone; ctrl-click forces enable AND resets the target to
    /// "Default Playback Device" (pid 0 deleted). Right-click is handled separately and opens
    /// the target-picker context menu.
    /// </summary>
    private void ListenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        if (!device.IsCaptureDevice) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl)
        {
            // Force-on with follow-default target. Matches the user's "literal default device"
            // expectation: the target follows whatever IS the default render endpoint at any
            // moment, rather than being pinned to today's choice.
            device.SetListenTarget(null, enable: true);
        }
        else
        {
            device.SetListenEnabled(!device.IsListeningToThisDevice);
        }
    }

    private void ListenButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        if (!device.IsCaptureDevice) return;

        OpenListenTargetMenu(fe, device);
        e.Handled = true;
    }

    /// <summary>
    /// Right-click on the device name OR the compact format label opens a picker of every
    /// (bit depth, sample rate) combination this endpoint accepts in shared mode. Mirrors
    /// mmsys.cpl's Advanced > Default Format dropdown but inline - the user doesn't have to
    /// leave the flyout. Wired to both labels so the gesture works on the bigger, easier-to-hit
    /// name target as well as the small format readout.
    /// </summary>
    private void DeviceFormatMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioDevice device) return;
        OpenDefaultFormatMenu(fe, device);
        e.Handled = true;
    }

    // Cap visible height to 8 items; the global ContextMenu template (App.xaml) wraps items in a
    // ScrollViewer with VerticalScrollBarVisibility=Auto, so anything above the cap scrolls instead
    // of clipping. Item-height math: FontSize 14 line-height (~18) + MenuItem Padding 6,6 (12) +
    // Top/Bottom gap rows 2+2 = ~34px; +8px slack covers the ContextMenu's Padding and the
    // ScrollViewer's bar gutter.
    private const double FormatMenuItemHeight = 34;
    private const int FormatMenuMaxVisibleItems = 8;

    /// <summary>
    /// Opens the default-format picker for an endpoint. ContextMenu lives in XAML as a
    /// FrameworkElement resource; the per-open item list is shoved onto the menu via its Tag so the
    /// XAML ItemsSource binding picks it up.
    /// </summary>
    private void OpenDefaultFormatMenu(FrameworkElement anchor, AudioDevice device)
    {
        List<(int Bits, int SampleRate)> formats = device.EnumerateSupportedFormats();
        if (formats.Count == 0) return;

        (int Channels, int Bits, int SampleRate)? current = device.GetCurrentFormat();
        int channels = current?.Channels ?? 0;

        List<FormatMenuItem> items = new(formats.Count);
        foreach ((int bits, int rate) in formats)
        {
            int capturedBits = bits;
            int capturedRate = rate;
            string label = $"{channels} channel, {bits} bit, {rate} Hz";
            bool isCurrent = current.HasValue && current.Value.Bits == bits && current.Value.SampleRate == rate;
            items.Add(new FormatMenuItem(
                label,
                isCurrent,
                new MenuRelayCommand(() => device.SetDeviceFormat(capturedBits, capturedRate))));
        }

        ContextMenu menu = (ContextMenu)FindResource("DefaultFormatContextMenu");
        menu.PlacementTarget = anchor;
        menu.Tag = new MenuItemsPayload<FormatMenuItem>(items);
        menu.MaxHeight = formats.Count > FormatMenuMaxVisibleItems
            ? FormatMenuMaxVisibleItems * FormatMenuItemHeight + 8
            : double.PositiveInfinity;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Opens the listen-target picker for a capture endpoint. First entry is "Default Playback
    /// Device" (null target = follow whichever endpoint is default). Subsequent entries are the
    /// active render endpoints, sorted alphabetically. Picking any entry writes both pid 0
    /// (target id) and pid 1 (enable=true) in a single property-store commit.
    /// </summary>
    private void OpenListenTargetMenu(FrameworkElement anchor, AudioDevice captureDevice)
    {
        string? currentTarget = captureDevice.ListenTargetDeviceID;

        List<ListenTargetMenuItem> items = new()
        {
            new ListenTargetMenuItem(
                Strings.Flyout_ListenMenu_DefaultPlaybackDevice,
                currentTarget == null,
                new MenuRelayCommand(() => captureDevice.SetListenTarget(null, enable: true))),
        };

        List<AudioDevice> renderTargets = new();
        foreach (AudioDevice d in _deviceManager.Devices)
        {
            if (d.DataFlow == Audio.Interop.EDataFlow.eRender && d.IsActive) renderTargets.Add(d);
        }
        renderTargets.Sort((a, b) => string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.CurrentCultureIgnoreCase));

        foreach (AudioDevice target in renderTargets)
        {
            string targetId = target.Id;
            items.Add(new ListenTargetMenuItem(
                target.FriendlyName,
                string.Equals(currentTarget, targetId, StringComparison.Ordinal),
                new MenuRelayCommand(() => captureDevice.SetListenTarget(targetId, enable: true))));
        }

        ContextMenu menu = (ContextMenu)FindResource("ListenTargetContextMenu");
        menu.PlacementTarget = anchor;
        menu.Tag = new MenuItemsPayload<ListenTargetMenuItem>(items);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Double-click on the device-name TextBlock enters inline rename mode. The sibling TextBox
    /// (same Grid cell) is seeded with the current FriendlyName, made visible, focused, and its
    /// text selected. Single clicks bubble through untouched so they don't disturb the row's
    /// existing hit testing on the icon button or the slider below.
    /// </summary>
    private void DeviceNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not TextBlock tb || tb.Tag is not AudioDevice device) return;

        System.Windows.Controls.TextBox? edit = FindSiblingNameEditor(tb);
        if (edit == null) return;

        edit.Text = device.FriendlyName;
        edit.Tag = device;
        tb.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Visible;
        edit.Focus();
        edit.SelectAll();
        e.Handled = true;
    }

    private void DeviceNameEdit_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not System.Windows.Controls.TextBox box) return;
        CommitDeviceNameEdit(box);
        e.Handled = true;
    }

    private void DeviceNameEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        // Enter commits by collapsing the box first, which fires LostKeyboardFocus a second time
        // as focus leaves; the visibility check is the guard against a duplicate commit.
        if (box.Visibility != Visibility.Visible) return;
        CommitDeviceNameEdit(box);
    }

    private static void CommitDeviceNameEdit(System.Windows.Controls.TextBox box)
    {
        AudioDevice? device = box.Tag as AudioDevice;
        box.Tag = null;
        box.Visibility = Visibility.Collapsed;

        TextBlock? label = FindSiblingNameLabel(box);
        if (label != null) label.Visibility = Visibility.Visible;

        // Empty / whitespace clears the property store override and the OS-synthesized name
        // returns - matches the Sound Control Panel rename gesture.
        device?.SetCustomFriendlyName(box.Text);
    }

    private static System.Windows.Controls.TextBox? FindSiblingNameEditor(FrameworkElement element)
    {
        if (element.Parent is not Grid parent) return null;
        foreach (object child in parent.Children)
        {
            if (child is System.Windows.Controls.TextBox box) return box;
        }
        return null;
    }

    private static TextBlock? FindSiblingNameLabel(FrameworkElement element)
    {
        if (element.Parent is not Grid parent) return null;
        foreach (object child in parent.Children)
        {
            if (child is TextBlock tb) return tb;
        }
        return null;
    }

    private void AppIcon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioAppGroup group) return;

        // Capture sessions have no useful per-app mute. Walk one ContentPresenter up to read
        // the parent cell's IsCapture - same hop the XAML "Cursor=Arrow on capture cell"
        // DataTrigger uses via RelativeSource. Inlined here rather than a named helper.
        DependencyObject? cursor = fe;
        while (cursor != null && (cursor is not FrameworkElement parent || parent.DataContext is not VolumeFlyoutCell))
        {
            cursor = VisualTreeHelper.GetParent(cursor);
        }
        if (cursor is FrameworkElement cellHost && cellHost.DataContext is VolumeFlyoutCell { IsCapture: true }) return;

        group.IsMuted = !group.IsMuted;
        e.Handled = true;
    }

    private void UndockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undockButtonDragOccurred)
        {
            _undockButtonDragOccurred = false;
            return;
        }

        if (_isUndocked) Redock();
        else UndockToSavedPosition();
    }

    private void UndockButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _undockButtonDragOccurred = false;
        double snapTolerance = CaptureDockedPosition();
        _dragHelper.BeginDrag(e.GetPosition(this), _dockedLeft, _dockedTop, snapTolerance);
        UndockButton.CaptureMouse();
    }

    private void UndockButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!UndockButton.IsMouseCaptured) return;

        (double naturalX, double naturalY) = _dragHelper.ComputeNatural(e.GetPosition(this));

        if (!_undockButtonDragOccurred)
        {
            if (!_dragHelper.ExceedsThreshold(naturalX, naturalY, DragThreshold)) return;

            _undockButtonDragOccurred = true;
            IsUndocked = true;
        }

        _dragHelper.ApplyDragPosition(naturalX, naturalY);
    }

    private void UndockButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_undockButtonDragOccurred) return;

        if (_dragHelper.IsCurrentlySnapped)
        {
            IsUndocked = false;
            if (_appSettings != null)
            {
                _appSettings.FlyoutUndocked = false;
                _appSettings.Save();
            }
            Left = _dockedLeft;
            Top = _dockedTop;
        }
        else SaveUndockedPosition();
    }

    private void RootCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isUndocked) return;
        if (sender is not IInputElement el) return;

        double snapTolerance = CaptureDockedPosition();
        _isDraggingFromBackground = true;
        _dragHelper.BeginDrag(e.GetPosition(this), _dockedLeft, _dockedTop, snapTolerance);

        Mouse.Capture(el);
        e.Handled = true;
    }

    private void RootCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingFromBackground) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        (double naturalX, double naturalY) = _dragHelper.ComputeNatural(e.GetPosition(this));
        _dragHelper.ApplyDragPosition(naturalX, naturalY);
    }

    private void RootCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingFromBackground) return;

        Mouse.Capture(null);
        _isDraggingFromBackground = false;

        if (_dragHelper.IsCurrentlySnapped) Redock();
        else SaveUndockedPosition();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ===========================================================================================
// Context-menu payload types. ContextMenu resources in VolumeFlyout.xaml bind ItemsSource to
// PlacementTarget.Tag.Items so we don't need to set ItemsSource imperatively on every open.
// ===========================================================================================

/// <summary>Wraps a list so XAML can resolve <c>Items</c> via property-path binding.</summary>
internal sealed class MenuItemsPayload<T>
{
    public List<T> Items { get; }
    public MenuItemsPayload(List<T> items) => Items = items;
}

/// <summary>One row in the default-format picker.</summary>
internal sealed record FormatMenuItem(string Label, bool IsCurrent, ICommand Command);

/// <summary>One row in the listen-target picker.</summary>
internal sealed record ListenTargetMenuItem(string Label, bool IsCurrent, ICommand Command);

/// <summary>
/// Minimal ICommand for menu-item click bindings. CanExecute is always true; the menu hides itself
/// on click and we just want the Action to fire on the routed Click.
/// </summary>
internal sealed class MenuRelayCommand : ICommand
{
    private readonly Action _execute;
    public MenuRelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
