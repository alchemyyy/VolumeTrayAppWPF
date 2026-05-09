using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Interop;
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
    // Step the device / session sliders by 2 percent per wheel notch
    // -- matches Windows' Volume mixer behavior (smooth fine-grained control without pop noise).
    private const double WheelVolumeStepPercent = 2.0;

    // Click-vs-drag threshold for the undock button. Anything under this travels back to a click,
    // so a tiny shake doesn't accidentally undock-and-save-position when the user really meant to toggle.
    private const double DragThreshold = 4;

    // Snap zone width as a fraction of the working area's smaller dimension, so the snap-back
    // feels equally generous on a 1080p laptop, a 4K desktop, and an ultrawide.
    private const double SnapTolerancePercent = 0.02;

    // Per-app feedback file. Picked because Windows Background.wav is the same wav SystemSounds.Beep
    // resolves to on default Windows installs, so the device-slider beep and the per-app slider beep
    // sound consistent. Falls back to silence if the file is missing.
    private const string AppFeedbackWavName = "Windows Background.wav";

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

    // Our own AppId, computed once with the same lower-cased-image-path scheme AudioSession uses.
    // Per-app slider feedback (PlayAppVolumeFeedback) routes through SoundPlayer / winmm, which
    // registers an audio session under our PID; without this filter, each cell would show its own
    // slider transiently while sound is playing. Null means we couldn't resolve our image path -
    // fall back to no filtering rather than guessing.
    private readonly string? _ownAppId;

    // Devices we have a PropertyChanged subscription on. Kept separately so add / remove
    // notifications from AudioDeviceManager.Devices can sync subscriptions without scanning twice.
    private readonly HashSet<AudioDevice> _subscribedDevices = new();

    // Cell wrappers indexed by device for incremental rebuild without disposing the wrapper on every
    // ordering change. Pruning happens when the device drops out of the visible list.
    private readonly Dictionary<AudioDevice, VolumeFlyoutCell> _cellsByDevice = new();

    private HwndSource? _hwndSource;
    private readonly AppSettings? _appSettings;

    // The layout style at the time the cells were last rebuilt. A flip in
    // FlyoutDeviceLayout requires force-rebuilding the cells (the DataTemplateSelector caches its
    // selection per-ContentPresenter, so we have to recreate the visuals to swap templates).
    private FlyoutDeviceLayoutStyle _renderedLayout;

    // Currently-captured slider during a track click drag. Null when no drag is in flight or the
    // active gesture is a thumb drag (which WPF handles natively).
    private Slider? _draggingSlider;

    // Per-app slider feedback. _wavTemplate holds the wav bytes loaded once at startup; each play
    // clones it, scales PCM samples in-place by the target app's slider value, and hands the bytes
    // to SoundPlayer. SoundPlayer routes through winmm.dll's PlaySound directly, which - unlike WPF's
    // MediaPlayer - doesn't depend on Windows Media Player and so works on Windows N installs.
    private byte[]? _wavTemplate;
    private int _wavDataOffset;
    private int _wavDataLength;
    private int _wavBitsPerSample;
    // Held across plays so the byte[] backing the in-flight async PlaySound isn't GC'd mid-playback,
    // and so a follow-up play disposes the prior player (which preempts its still-playing sound).
    private System.Media.SoundPlayer? _currentAppSound;

    // Trailing-edge debouncer for the volume-change ding. Each scroll/wheel/drag-end calls RunAsync;
    // the payload polls HasReplacement during its dwell and bails the moment a fresher event lands,
    // so the ding only fires once the dwell elapses with no new event arriving. Cooldown is 0 because
    // the dwell itself IS the rate-limit. Two keys keep device and per-app feedback independent.
    private readonly AsyncThrottler<string> _feedbackThrottler = new(0, StringComparer.Ordinal);
    private const string DeviceDingThrottleKey = "device";
    private const string AppDingThrottleKey = "app";

    // Slice size for the dwell's HasReplacement poll. Smaller = ding fires closer to "exactly 150ms
    // after the last event"; larger = fewer wakeups. 10ms is well below human perception.
    private const int DingDwellPollSliceMs = 10;

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

        // Resolve our own AppId once. Matches AudioSession's image-path-lower-invariant scheme
        // so an exact string compare in the cell filter suffices.
        string? ownPath = ProcessHelper.GetProcessImagePath((uint)Environment.ProcessId);
        _ownAppId = string.IsNullOrEmpty(ownPath) ? null : ownPath.ToLowerInvariant();

        InitializeComponent();
        DataContext = this;

        _dragHelper = new WindowDragHelper(this);

        IsUndocked = _appSettings is
        {
            FlyoutUndocked: true,
            FlyoutHasSavedPosition: true,
            AllowFlyoutUndock: true,
            RestoreFlyoutUndockedOnStartup: true
        };

        _renderedLayout = _appSettings?.FlyoutDeviceLayout ?? FlyoutDeviceLayoutStyle.AppsAboveDevice;

        ((INotifyCollectionChanged)_deviceManager.Devices).CollectionChanged += OnDevicesCollectionChanged;
        SyncDeviceSubscriptions();
        RebuildCellList(forceFullRebuild: false);

        if (_appSettings != null) _appSettings.Changed += OnAppSettingsChanged;

        SourceInitialized += OnFlyoutSourceInitialized;
        Closed += OnFlyoutClosed;

        EnsureAppFeedbackData();
    }

    /// <summary>
    /// Settings change handler.
    /// Pulls the flyout back to docked when AllowFlyoutUndock flips off mid-session, then rebuilds
    /// the cell list so any sort / layout / visibility setting change is reflected without waiting
    /// for the next device event. A layout flip force-rebuilds the cells so the DataTemplateSelector
    /// picks up the new mode.
    /// </summary>
    private void OnAppSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isUndocked && _appSettings?.AllowFlyoutUndock == false) Redock();
            OnPropertyChanged(nameof(AllowFlyoutUndock));

            FlyoutDeviceLayoutStyle currentLayout = _appSettings?.FlyoutDeviceLayout
                ?? FlyoutDeviceLayoutStyle.AppsAboveDevice;
            bool layoutChanged = currentLayout != _renderedLayout;
            _renderedLayout = currentLayout;

            RebuildCellList(forceFullRebuild: layoutChanged);
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

        foreach (VolumeFlyoutCell cell in _cellsByDevice.Values) cell.Dispose();
        _cellsByDevice.Clear();
        _cells.Clear();

        // Dispose the throttler before the SoundPlayer so any in-flight dwell exits via its
        // shutdown token before the payload tries to dispatch a play onto a torn-down dispatcher.
        try { _feedbackThrottler.Dispose(); }
        catch { /* shutdown best-effort */ }

        if (_currentAppSound != null)
        {
            try { _currentAppSound.Stop(); _currentAppSound.Dispose(); }
            catch { /* shutdown best-effort */ }
            _currentAppSound = null;
        }
        _wavTemplate = null;
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
        RebuildCellList(forceFullRebuild: false);
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
        RebuildCellList(forceFullRebuild: false);
    }

    /// <summary>
    /// Recomputes the visible cell list from the manager's device list and the current settings.
    /// Diff-based - existing cells are kept (preserving their internal state); only cells for
    /// removed-from-visible devices are disposed. <paramref name="forceFullRebuild"/> wipes the
    /// list and rebuilds from scratch so the DataTemplateSelector re-evaluates each cell - used
    /// when FlyoutDeviceLayout flips mid-session.
    /// </summary>
    private void RebuildCellList(bool forceFullRebuild)
    {
        if (_appSettings == null)
        {
            // Without settings, fall back to listing every render device in enumeration order.
            ApplyOrderedDevices(_deviceManager.Devices.Where(d => d.DataFlow == Audio.Interop.EDataFlow.eRender).ToList(),
                forceFullRebuild);
            return;
        }

        List<AudioDevice> ordered = FlyoutDeviceOrdering.Build(_deviceManager.Devices, _appSettings);
        ApplyOrderedDevices(ordered, forceFullRebuild);
    }

    private void ApplyOrderedDevices(List<AudioDevice> ordered, bool forceFullRebuild)
    {
        if (forceFullRebuild)
        {
            // Layout flipped - DataTemplateSelector caches per-ContentPresenter, so we have to
            // tear the visuals down to swap templates.
            _cells.Clear();
        }

        // Step 1: prune cells whose device left the visible list.
        HashSet<AudioDevice> orderedSet = new(ordered);
        foreach (KeyValuePair<AudioDevice, VolumeFlyoutCell> kv in _cellsByDevice.ToArray())
        {
            if (!orderedSet.Contains(kv.Key))
            {
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
                cell = new VolumeFlyoutCell(device, _ownAppId);
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

        OnPropertyChanged(nameof(HasCells));
    }

    /// <summary>
    /// Positions the flyout. Anchors to the bottom-right of the working area when docked, or
    /// restores the user's saved undocked coordinates when undocked.
    /// </summary>
    public void PositionNearTray()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double padding = 8;
        _dockedLeft = workArea.Right - Width - padding;
        _dockedTop = workArea.Bottom - ActualHeight - padding;

        if (_isUndocked && _appSettings?.FlyoutHasSavedPosition == true)
        {
            Left = _appSettings.FlyoutLeft;
            Top = _appSettings.FlyoutTop;
        }
        else
        {
            Left = _dockedLeft;
            Top = _dockedTop;
        }
    }

    private double CaptureDockedPosition()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double padding = 8;
        _dockedLeft = workArea.Right - Width - padding;
        _dockedTop = workArea.Bottom - ActualHeight - padding;
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
        base.Show();
        UpdateLayout();
        PositionNearTray();
        Activate();
        Focus();
        _deviceManager.StartMetering();
    }

    /// <summary>
    /// Shows the flyout without taking foreground/keyboard focus from the caller. Used when
    /// SettingsWindow re-opens the flyout on its own activation.
    /// </summary>
    public void ShowWithoutActivating()
    {
        bool previousShowActivated = ShowActivated;
        ShowActivated = false;
        try { base.Show(); }
        finally { ShowActivated = previousShowActivated; }

        UpdateLayout();
        PositionNearTray();
        _deviceManager.StartMetering();
    }

    /// <summary>Hides the flyout and stops peak metering.</summary>
    public new void Hide()
    {
        _deviceManager.StopMetering();
        base.Hide();
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
    /// Mouse-down on a slider. Thumb hits go to WPF's native track for smooth dragging; track-body
    /// hits start a captured drag here so the cursor jumps to the click point and the thumb keeps
    /// following until release.
    /// </summary>
    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        Track? track = FindVisualChild<Track>(slider);
        if (track?.Thumb == null) return;

        Rect thumbBounds = new(
            track.Thumb.TranslatePoint(new System.Windows.Point(0, 0), slider),
            new System.Windows.Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));

        if (thumbBounds.Contains(e.GetPosition(slider))) return;

        _draggingSlider = slider;
        slider.CaptureMouse();
        UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
        e.Handled = true;
    }

    private void Slider_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Slider slider || _draggingSlider != slider) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopDragging(slider);
            return;
        }

        Track? track = FindVisualChild<Track>(slider);
        if (track != null) UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
    }

    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        if (_draggingSlider == slider) StopDragging(slider);

        // Branch on DataContext type rather than element identity: the slider's data context is the
        // device wrapper for device sliders (set on the device-row template) and the AudioAppGroup
        // for session sliders.
        if (slider.DataContext is AudioDevice) PlayDeviceVolumeFeedback();
        else if (slider.DataContext is AudioAppGroup group) PlayAppVolumeFeedback(group.Volume);
    }

    private void Slider_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Intentional no-op: a captured drag must keep tracking even when the cursor leaves the slider's
        // bounds (matches BrightnessFlyout). Release happens on PreviewMouseLeftButtonUp.
    }

    private static void UpdateSliderValueFromMousePosition(Slider slider, Track track, System.Windows.Point position)
    {
        double thumbWidth = track.Thumb?.ActualWidth ?? 0;
        double trackStart = thumbWidth / 2.0;
        double trackEnd = slider.ActualWidth - thumbWidth / 2.0;
        double trackLength = trackEnd - trackStart;
        if (trackLength <= 0) return;

        double adjustedX = position.X - trackStart;
        double percentage = Math.Max(0, Math.Min(1, adjustedX / trackLength));
        slider.Value = slider.Minimum + (slider.Maximum - slider.Minimum) * percentage;
    }

    private void StopDragging(Slider slider)
    {
        _draggingSlider = null;
        slider.ReleaseMouseCapture();
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

    private void SessionRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not AudioAppGroup group) return;

        double currentPercent = group.Volume * 100.0;
        double next = currentPercent + (e.Delta > 0 ? WheelVolumeStepPercent : -WheelVolumeStepPercent);
        if (next < 0) next = 0;
        else if (next > 100) next = 100;

        bool changed = Math.Abs(next - currentPercent) > 0.001;
        group.Volume = (float)(next / 100.0);
        if (changed) PlayAppVolumeFeedback(group.Volume);
        e.Handled = true;
    }

    /// <summary>
    /// Mouse wheel over a device row scrolls that device's volume. The owning device is resolved
    /// via the row's DataContext (the device-row content control sets DataContext to the cell's
    /// Device, so scroll events bubble back here with that context).
    /// </summary>
    private void DeviceRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        AudioDevice? device = ResolveDeviceFromContext(fe);
        if (device == null) return;

        double currentPercent = device.Volume * 100.0;
        double next = currentPercent + (e.Delta > 0 ? WheelVolumeStepPercent : -WheelVolumeStepPercent);
        if (next < 0) next = 0;
        else if (next > 100) next = 100;

        bool changed = Math.Abs(next - currentPercent) > 0.001;
        device.Volume = (float)(next / 100.0);
        if (changed) PlayDeviceVolumeFeedback();
        e.Handled = true;
    }

    /// <summary>
    /// Walks the visual tree from a click target up to a FrameworkElement whose DataContext is an
    /// AudioDevice. Used by the device-row click / wheel handlers since the row template's DataContext
    /// is set to the cell's Device but the click target itself may be a child Grid that inherits the
    /// same DataContext.
    /// </summary>
    private static AudioDevice? ResolveDeviceFromContext(FrameworkElement element)
    {
        FrameworkElement? cursor = element;
        while (cursor != null)
        {
            if (cursor.DataContext is AudioDevice device) return device;
            cursor = VisualTreeHelper.GetParent(cursor) as FrameworkElement;
        }
        return null;
    }

    private void PlayDeviceVolumeFeedback()
    {
        if (_appSettings?.PlayDeviceVolumeChangeSound != true) return;

        Dispatcher uiDispatcher = Dispatcher;
        _ = _feedbackThrottler.RunAsync(DeviceDingThrottleKey, async ctx =>
        {
            if (!await DwellWithReplacementBailAsync(ctx, TimeConstants.VolumeFeedbackDingDelayMs).ConfigureAwait(false)) return;
            try
            {
                await uiDispatcher.InvokeAsync(static () =>
                {
                    try { System.Media.SystemSounds.Beep.Play(); }
                    catch { /* sound playback is best-effort */ }
                });
            }
            catch { /* dispatcher torn down */ }
        });
    }

    private void PlayAppVolumeFeedback(float scalarVolume)
    {
        if (_appSettings?.PlayAppVolumeChangeSound != true) return;

        EnsureAppFeedbackData();
        if (_wavTemplate == null) return;

        // Capture scalarVolume in the closure: latest-pending-wins on the throttler means the payload
        // that ultimately runs is the most recent one queued, so the played volume reflects the user's
        // latest position rather than whatever it was when the gesture started.
        Dispatcher uiDispatcher = Dispatcher;
        _ = _feedbackThrottler.RunAsync(AppDingThrottleKey, async ctx =>
        {
            if (!await DwellWithReplacementBailAsync(ctx, TimeConstants.VolumeFeedbackDingDelayMs).ConfigureAwait(false)) return;
            try { await uiDispatcher.InvokeAsync(() => PlayAppFeedbackNow(scalarVolume)); }
            catch { /* dispatcher torn down */ }
        });
    }

    private void PlayAppFeedbackNow(float scalarVolume)
    {
        if (_wavTemplate == null) return;

        try
        {
            byte[] scaled = (byte[])_wavTemplate.Clone();
            ScalePcmSamples(scaled, _wavDataOffset, _wavDataLength, _wavBitsPerSample,
                Math.Clamp(scalarVolume, 0f, 1f));

            MemoryStream stream = new(scaled, writable: false);
            System.Media.SoundPlayer player = new(stream);
            player.Play();

            _currentAppSound?.Dispose();
            _currentAppSound = player;
        }
        catch { /* feedback is best-effort */ }
    }

    // Waits up to <paramref name="totalMs"/> in poll-sized slices, returning false the moment a fresher
    // payload is queued for the same key OR cancellation is signalled. Returns true only when the full
    // dwell elapses without a replacement -- the caller treats that as "ok to fire the ding".
    private static async Task<bool> DwellWithReplacementBailAsync(IThrottlerContext ctx, int totalMs)
    {
        int waited = 0;
        while (waited < totalMs)
        {
            if (ctx.HasReplacement) return false;
            int slice = Math.Min(DingDwellPollSliceMs, totalMs - waited);
            try { await Task.Delay(slice, ctx.CancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            waited += slice;
        }
        return !ctx.HasReplacement;
    }

    private void EnsureAppFeedbackData()
    {
        if (_wavTemplate != null) return;

        string wavPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Media", AppFeedbackWavName);
        if (!File.Exists(wavPath)) return;

        try
        {
            byte[] bytes = File.ReadAllBytes(wavPath);
            if (!ParseWavHeader(bytes, out int dataOffset, out int dataLength, out int bitsPerSample)) return;
            _wavTemplate = bytes;
            _wavDataOffset = dataOffset;
            _wavDataLength = dataLength;
            _wavBitsPerSample = bitsPerSample;
        }
        catch { /* file disappeared / unreadable - stay silent */ }
    }

    private static bool ParseWavHeader(byte[] data, out int dataOffset, out int dataLength, out int bitsPerSample)
    {
        dataOffset = 0; dataLength = 0; bitsPerSample = 0;

        if (data.Length < 12) return false;
        if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
        if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            int chunkSize = BitConverter.ToInt32(data, pos + 4);
            int chunkData = pos + 8;
            if (chunkSize < 0 || chunkData + chunkSize > data.Length) return false;

            if (data[pos] == 'f' && data[pos + 1] == 'm' && data[pos + 2] == 't' && data[pos + 3] == ' ')
            {
                if (chunkSize < 16) return false;
                bitsPerSample = BitConverter.ToInt16(data, chunkData + 14);
            }
            else if (data[pos] == 'd' && data[pos + 1] == 'a' && data[pos + 2] == 't' && data[pos + 3] == 'a')
            {
                dataOffset = chunkData;
                dataLength = chunkSize;
                return bitsPerSample > 0;
            }

            pos = chunkData + chunkSize;
            if ((chunkSize & 1) != 0) pos++;
        }
        return false;
    }

    private static void ScalePcmSamples(byte[] data, int offset, int length, int bitsPerSample, float volume)
    {
        if (volume >= 0.999f) return;

        int end = offset + length;
        switch (bitsPerSample)
        {
            case 16:
                for (int i = offset; i + 1 < end; i += 2)
                {
                    short sample = (short)(data[i] | (data[i + 1] << 8));
                    int scaled = (int)(sample * volume);
                    data[i] = (byte)(scaled & 0xFF);
                    data[i + 1] = (byte)((scaled >> 8) & 0xFF);
                }
                break;
            case 24:
                for (int i = offset; i + 2 < end; i += 3)
                {
                    int sample = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
                    if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                    int scaled = (int)(sample * volume);
                    if (scaled > 0x7FFFFF) scaled = 0x7FFFFF;
                    else if (scaled < -0x800000) scaled = -0x800000;
                    data[i] = (byte)(scaled & 0xFF);
                    data[i + 1] = (byte)((scaled >> 8) & 0xFF);
                    data[i + 2] = (byte)((scaled >> 16) & 0xFF);
                }
                break;
            case 32:
                for (int i = offset; i + 3 < end; i += 4)
                {
                    int sample = BitConverter.ToInt32(data, i);
                    int scaled = (int)(sample * volume);
                    data[i] = (byte)(scaled & 0xFF);
                    data[i + 1] = (byte)((scaled >> 8) & 0xFF);
                    data[i + 2] = (byte)((scaled >> 16) & 0xFF);
                    data[i + 3] = (byte)((scaled >> 24) & 0xFF);
                }
                break;
        }
    }

    /// <summary>
    /// Toggles mute on the device whose row hosts this button. The button's DataContext is the
    /// AudioDevice (the row template sets DataContext to cell.Device).
    /// </summary>
    private void DeviceMute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        AudioDevice? device = ResolveDeviceFromContext(fe);
        if (device == null) return;
        device.IsMuted = !device.IsMuted;
    }

    /// <summary>
    /// Device-icon button click. Plain -> toggle enable/disable; Ctrl -> set as default and open
    /// classic device properties; Shift -> set as default communications. The owning device comes
    /// from the click target's DataContext.
    /// </summary>
    private void DeviceIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        AudioDevice? device = ResolveDeviceFromContext(fe);
        if (device == null) return;

        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (shift) device.SetAsDefaultCommunications();
        else if (ctrl)
        {
            device.SetAsDefault();
            DeviceShellLinks.OpenDeviceProperties(device);
        }
        else device.SetEnabled(!device.IsActive);
    }

    /// <summary>Footer Settings button. Hands off to the host; App.xaml.cs opens SettingsWindow.</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void DeviceIcon_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        AudioDevice? device = ResolveDeviceFromContext(fe);
        if (device == null) return;
        DeviceShellLinks.OpenDeviceProperties(device);
        e.Handled = true;
    }

    private void AppIcon_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not AudioAppGroup group) return;
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
