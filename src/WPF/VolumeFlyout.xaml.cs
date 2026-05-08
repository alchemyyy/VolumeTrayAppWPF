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
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.WPF.Utils;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// Tray flyout that surfaces per-app session volumes plus the default-device endpoint volume.
/// DataContext is the window itself; properties below are the bindable surface area.
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

    private readonly AudioDeviceManager _deviceManager;
    // The visible list aggregates groups across every active render device, filtered to skip system
    // sounds and expired groups. Cross-device aggregation is required because Discord (and most
    // comms apps) routes voice to the user's Communications-role default endpoint, which Windows
    // lets the user point at a different device than the Multimedia-role default. Watching only the
    // multimedia default - the previous behavior - hid Discord and any other comms-routed app.
    // Dedup by AppId picks the first device-level group per app so a user with the same app live on
    // two endpoints sees one row instead of two.
    // Type is AudioAppGroup so the slider DataTemplate fans volume changes out to all child sessions
    // for apps like Discord that spawn several child processes.
    private readonly ObservableCollection<AudioAppGroup> _sessions = [];

    // Devices we have a Groups.CollectionChanged subscription on. Kept separately so add / remove
    // notifications from AudioDeviceManager.Devices can sync subscriptions without scanning twice.
    private readonly HashSet<AudioDevice> _subscribedDevices = new();
    private AudioDevice? _trackedDevice;
    private HwndSource? _hwndSource;
    private readonly AppSettings? _appSettings;

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

    public ReadOnlyObservableCollection<AudioAppGroup> Sessions { get; }

    public AudioDevice? DefaultDevice
    {
        get => _trackedDevice;
        private set
        {
            if (ReferenceEquals(_trackedDevice, value)) return;
            _trackedDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDefaultDevice));
        }
    }

    public bool HasDefaultDevice => _trackedDevice != null;
    public bool HasSessions => _sessions.Count > 0;

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
        Sessions = new ReadOnlyObservableCollection<AudioAppGroup>(_sessions);

        InitializeComponent();
        DataContext = this;

        _dragHelper = new WindowDragHelper(this);

        // Restore the undocked state from settings.
        // Requires a saved position too, otherwise the first session after the user toggled the flag
        // would have nowhere to position the window.
        // Honors the AllowFlyoutUndock master gate so disabling the feature can't leave us undocked,
        // and RestoreFlyoutUndockedOnStartup so users can opt out of session restoration.
        IsUndocked = _appSettings is
        {
            FlyoutUndocked: true,
            FlyoutHasSavedPosition: true,
            AllowFlyoutUndock: true,
            RestoreFlyoutUndockedOnStartup: true
        };

        _deviceManager.PropertyChanged += OnDeviceManagerPropertyChanged;
        AttachToDevice(_deviceManager.DefaultDevice);

        // Subscribe to the manager's device list and to every existing device's group list so the
        // session aggregation stays live. Devices already created during AudioDeviceManager's
        // constructor are present here, so SyncDeviceSubscriptions seeds the subscriptions before
        // the first RebuildSessionList.
        ((INotifyCollectionChanged)_deviceManager.Devices).CollectionChanged += OnDevicesCollectionChanged;
        SyncDeviceSubscriptions();
        RebuildSessionList();

        if (_appSettings != null) _appSettings.Changed += OnAppSettingsChanged;

        SourceInitialized += OnFlyoutSourceInitialized;
        Closed += OnFlyoutClosed;

        // Pre-load the per-app feedback wav so the first slider interaction doesn't pay the file read.
        EnsureAppFeedbackData();
    }

    /// <summary>
    /// Master-gate enforcement and binding refresh.
    /// Pulls the flyout back to docked when AllowFlyoutUndock flips off mid-session so the user isn't
    /// stranded with a free-floating window and no visible button to redock it. Also re-fires the
    /// AllowFlyoutUndock notification so the undock-button visibility updates without waiting for a reopen.
    /// </summary>
    private void OnAppSettingsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isUndocked && _appSettings?.AllowFlyoutUndock == false) Redock();
            OnPropertyChanged(nameof(AllowFlyoutUndock));
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
        _deviceManager.PropertyChanged -= OnDeviceManagerPropertyChanged;
        ((INotifyCollectionChanged)_deviceManager.Devices).CollectionChanged -= OnDevicesCollectionChanged;
        foreach (AudioDevice d in _subscribedDevices)
        {
            ((INotifyCollectionChanged)d.Groups).CollectionChanged -= OnDeviceGroupsChanged;
        }
        _subscribedDevices.Clear();
        if (_appSettings != null) _appSettings.Changed -= OnAppSettingsChanged;
        DetachFromDevice();
        if (_currentAppSound != null)
        {
            try { _currentAppSound.Stop(); _currentAppSound.Dispose(); }
            catch { /* shutdown best-effort */ }
            _currentAppSound = null;
        }
        _wavTemplate = null;
    }

    private void OnDeviceManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioDeviceManager.DefaultDevice))
            AttachToDevice(_deviceManager.DefaultDevice);
    }

    /// <summary>
    /// Update which device the master volume slider in the flyout header binds to. Does NOT touch
    /// the session list - that aggregates across every device via the Devices subscription path.
    /// Called at startup with the multimedia default and again on every OnDefaultDeviceChanged.
    /// </summary>
    private void AttachToDevice(AudioDevice? device)
    {
        if (ReferenceEquals(_trackedDevice, device)) return;
        DefaultDevice = device;
    }

    /// <summary>
    /// Tear down state owned by the flyout when the host is shutting down. Session-list subscriptions
    /// are cleared in OnFlyoutClosed; this method now only resets the tracked device reference.
    /// </summary>
    private void DetachFromDevice()
    {
        DefaultDevice = null;
        _sessions.Clear();
        OnPropertyChanged(nameof(HasSessions));
    }

    /// <summary>
    /// Sync our per-device Groups subscription set against the manager's current device list.
    /// Called once at startup to seed subscriptions to existing devices, and again on every Devices
    /// CollectionChanged to add subs for new endpoints / drop subs for removed ones. Disposed
    /// AudioDevice instances stop firing Groups events, so leaving a stale handler is harmless,
    /// but we explicitly unsubscribe to avoid keeping the device alive via the delegate chain.
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
                ((INotifyCollectionChanged)d.Groups).CollectionChanged -= OnDeviceGroupsChanged;
                _subscribedDevices.Remove(d);
            }
        }

        // Subscribe devices that are new since the last sync.
        for (int i = 0; i < live.Length; i++)
        {
            AudioDevice d = live[i];
            if (_subscribedDevices.Add(d))
            {
                ((INotifyCollectionChanged)d.Groups).CollectionChanged += OnDeviceGroupsChanged;
            }
        }
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncDeviceSubscriptions();
        RebuildSessionList();
        if (IsVisible) PositionNearTray();
    }

    private void OnDeviceGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildSessionList();

        // SizeToContent reflows automatically; re-anchor so the bottom edge stays at the work-area corner.
        if (IsVisible) PositionNearTray();
    }

    /// <summary>
    /// Aggregate the visible session list from every active render device. Filters out system sounds
    /// and groups whose state is Expired (every session inside has expired). Dedup by AppId across
    /// devices ensures an app live on two endpoints (rare - usually only when the user has the same
    /// app pinned to multiple outputs) shows as a single row driven by the first device's group.
    /// </summary>
    private void RebuildSessionList()
    {
        _sessions.Clear();

        HashSet<string> seenAppIds = new(StringComparer.Ordinal);
        foreach (AudioDevice device in _deviceManager.Devices)
        {
            foreach (AudioAppGroup g in device.Groups)
            {
                if (g.State == Audio.Interop.AudioSessionState.Expired) continue;
                if (g.IsSystemSounds) continue;
                if (g.Sessions.Count == 0) continue;
                if (!seenAppIds.Add(g.AppId)) continue;
                _sessions.Add(g);
            }
        }

        OnPropertyChanged(nameof(HasSessions));
    }

    /// <summary>
    /// Positions the flyout. Anchors to the bottom-right of the working area when docked,
    /// or restores the user's saved undocked coordinates when undocked. The docked corner is always
    /// recomputed and cached so a subsequent drag's snap-back check has the right reference even
    /// if Window dimensions changed.
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

    /// <summary>
    /// Snapshots the docked corner and the snap tolerance without moving the window.
    /// Called at drag start so the snap-back-on-release check uses a stable reference even if the user
    /// drags across a DPI boundary or the working area shifts mid-gesture. Returns the snap tolerance
    /// so the drag helper can be armed in one call without re-reading WorkArea.
    /// </summary>
    private double CaptureDockedPosition()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double padding = 8;
        _dockedLeft = workArea.Right - Width - padding;
        _dockedTop = workArea.Bottom - ActualHeight - padding;
        return Math.Min(workArea.Width, workArea.Height) * SnapTolerancePercent;
    }

    /// <summary>
    /// Returns the flyout to docked behavior.
    /// Called on tray click, on undock-button click while undocked, and after a drag releases inside
    /// the snap zone. Doesn't clear the saved position: a subsequent click of the undock button
    /// restores the user's last manual placement.
    /// </summary>
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

    /// <summary>
    /// Click-only undock path. Flips state and moves the window to the last saved position.
    /// With no saved position yet, the window stays where it is so the user can drag it from there.
    /// </summary>
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

    /// <summary>
    /// Persists the window's current position as the saved undocked location.
    /// Called on drag-release outside the snap zone for both the button-drag and background-drag gestures.
    /// </summary>
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
        // Height changes are common - sessions arrive / depart, device row hides when no default exists.
        // Re-anchor so the bottom edge stays pinned to the work-area corner.
        if (IsVisible && sizeInfo.HeightChanged) PositionNearTray();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Undocked acts like a real window - stays open across focus changes.
        // The user dismisses it by redocking (button or tray click), at which point the docked path's
        // auto-hide-on-deactivate behavior resumes.
        if (_isUndocked) return;

        Hide();
        FlyoutDeactivated?.Invoke();
    }

    /// <summary>
    /// Mouse-down on a slider. Thumb hits go to WPF's native track for smooth dragging;
    /// track-body hits start a captured drag here so the cursor jumps to the click point and the
    /// thumb keeps following until release. Mirrors BrightnessFlyout's pattern.
    /// </summary>
    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        Track? track = FindVisualChild<Track>(slider);
        if (track?.Thumb == null) return;

        Rect thumbBounds = new(
            track.Thumb.TranslatePoint(new System.Windows.Point(0, 0), slider),
            new System.Windows.Size(track.Thumb.ActualWidth, track.Thumb.ActualHeight));

        if (thumbBounds.Contains(e.GetPosition(slider)))
        {
            // Click landed on the thumb - WPF's Track owns the drag from here.
            return;
        }

        // Track click: jump to position and keep the value following the cursor until release.
        // CaptureMouse keeps the move events flowing even when the cursor leaves the slider bounds.
        _draggingSlider = slider;
        slider.CaptureMouse();
        UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
        e.Handled = true;
    }

    private void Slider_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Only act on the captured slider; thumb-drags don't go through here.
        if (sender is not Slider slider || _draggingSlider != slider) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // Lost the button somewhere off-window without a release event arriving here; clean up.
            StopDragging(slider);
            return;
        }

        Track? track = FindVisualChild<Track>(slider);
        if (track != null) UpdateSliderValueFromMousePosition(slider, track, e.GetPosition(slider));
    }

    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        WPFLog.Log($"Slider_PreviewMouseLeftButtonUp: senderType={sender?.GetType().Name ?? "null"}");
        if (sender is not Slider slider) return;

        WPFLog.Log($"Slider mouse-up: isDevice={slider == DeviceVolumeSlider} dataCtxType={slider.DataContext?.GetType().Name ?? "null"}");

        // Track-click drags route through StopDragging; thumb drags don't (WPF's Track owns the gesture)
        // but still arrive here on release. Both paths feed the release-feedback beep below.
        if (_draggingSlider == slider) StopDragging(slider);

        if (slider == DeviceVolumeSlider) PlayDeviceVolumeFeedback();
        else if (slider.DataContext is AudioAppGroup group) PlayAppVolumeFeedback(group.Volume);
    }

    private void Slider_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Intentional no-op: a captured drag must keep tracking even when the cursor leaves the slider's
        // bounds (matches BrightnessFlyout). Release happens on PreviewMouseLeftButtonUp.
    }

    /// <summary>
    /// Sets the slider's value to the percentage represented by the cursor's X position, accounting
    /// for the half-thumb-width inset on each end of the usable track. Float-precision: no rounding.
    /// </summary>
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

    /// <summary>
    /// Mouse wheel over a session row scrolls that session's volume.
    /// Step matches the device-row scroll so the family of controls feels consistent.
    /// </summary>
    private void SessionRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        WPFLog.Log($"SessionRow_PreviewMouseWheel: senderType={sender?.GetType().Name ?? "null"} tagType={(sender as FrameworkElement)?.Tag?.GetType().Name ?? "null"}");
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
    /// Mouse wheel over the device row scrolls the default device's volume.
    /// </summary>
    private void DeviceRow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        AudioDevice? device = _trackedDevice;
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
    /// Plays the Windows default-beep system sound as feedback for a discrete device-volume change.
    /// Gated by <see cref="AppSettings.PlayDeviceVolumeChangeSound"/>; per-app sliders never call in here,
    /// so the feedback stays scoped to the device row only - matches EarTrumpet and the OS volume slider.
    /// MessageBeep (which SystemSounds wraps) is async and self-throttling, so a fast wheel scroll won't
    /// queue an audible backlog.
    /// </summary>
    private void PlayDeviceVolumeFeedback()
    {
        if (_appSettings?.PlayDeviceVolumeChangeSound != true) return;
        try { System.Media.SystemSounds.Beep.Play(); }
        catch { /* sound playback is best-effort; never let audio feedback break the UI gesture */ }
    }

    /// <summary>
    /// Plays the per-app feedback wav scaled to <paramref name="scalarVolume"/> (the target app's
    /// slider value). Each call clones the wav template, multiplies its PCM samples by the volume
    /// scalar, and hands the buffer to SoundPlayer; the prior player (if any) is disposed so its
    /// in-flight playback is preempted - matches Windows' "each press starts a fresh sound" model
    /// and keeps a wheel-spam from queueing a backlog of overlapping beeps.
    /// </summary>
    private void PlayAppVolumeFeedback(float scalarVolume)
    {
        WPFLog.Log($"PlayAppVolumeFeedback ENTRY: vol={scalarVolume:F2} settingsNull={_appSettings == null} setting={_appSettings?.PlayAppVolumeChangeSound}");
        if (_appSettings?.PlayAppVolumeChangeSound != true) return;

        EnsureAppFeedbackData();
        WPFLog.Log($"PlayAppVolumeFeedback: vol={scalarVolume:F2} template={(_wavTemplate?.Length ?? -1)} bits={_wavBitsPerSample} dataOff={_wavDataOffset} dataLen={_wavDataLength}");
        if (_wavTemplate == null) return;

        try
        {
            byte[] scaled = (byte[])_wavTemplate.Clone();
            ScalePcmSamples(scaled, _wavDataOffset, _wavDataLength, _wavBitsPerSample,
                Math.Clamp(scalarVolume, 0f, 1f));

            MemoryStream stream = new(scaled, writable: false);
            System.Media.SoundPlayer player = new(stream);
            player.Play();
            WPFLog.Log("PlayAppVolumeFeedback: SoundPlayer.Play() returned");

            // Swap _currentAppSound only after the new play started so the previous byte[] stays
            // referenced (kept alive) until the OS has had a chance to consume it on its own thread.
            _currentAppSound?.Dispose();
            _currentAppSound = player;
        }
        catch (Exception ex) { WPFLog.Log($"PlayAppVolumeFeedback threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Reads the wav into memory once and parses just enough of the RIFF header to know the data-chunk
    /// span and bit depth - the only inputs the per-play sample scaler needs. Idempotent.
    /// </summary>
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
        catch { /* file disappeared / unreadable - stay silent rather than crashing on volume change */ }
    }

    /// <summary>
    /// Walks the RIFF chunk list to locate the fmt and data chunks. Returns false on any structural
    /// issue so the caller falls back to silence rather than playing garbage at full volume.
    /// </summary>
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

            // RIFF chunks pad to even byte boundaries; advance past the pad byte if size is odd.
            pos = chunkData + chunkSize;
            if ((chunkSize & 1) != 0) pos++;
        }
        return false;
    }

    /// <summary>
    /// Multiplies every PCM sample in the data span by <paramref name="volume"/> in-place.
    /// 16-bit signed PCM is the path the default Windows install actually exercises; 24-bit and 32-bit
    /// signed PCM are supported defensively in case a custom sound theme has swapped the wav.
    /// Unsupported bit depths fall through unscaled - they'll play at full volume rather than silently
    /// dropping the sound, which is the right failure mode for audible feedback.
    /// </summary>
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
                    // Sign-extend the 24-bit value into the upper byte of the int.
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
    /// Toggles mute on the default device. Bound to the speaker-glyph button in the device row.
    /// </summary>
    private void DeviceMute_Click(object sender, RoutedEventArgs e)
    {
        AudioDevice? device = _trackedDevice;
        if (device == null) return;
        device.IsMuted = !device.IsMuted;
    }

    /// <summary>
    /// Stub for the device-picker caret. The dropdown UI is a follow-up feature; for now the click
    /// is a no-op so the chrome reads correctly without committing to a half-finished menu.
    /// </summary>
    private void DeviceCaret_Click(object sender, RoutedEventArgs e)
    {
        // Intentionally empty - device-switching popup will land in a later iteration.
    }

    /// <summary>
    /// Footer Settings button. Hands off to the host via <see cref="SettingsRequested"/>;
    /// App.xaml.cs opens the SettingsWindow there. Focus moves off the flyout as a side effect,
    /// which lets the docked-mode auto-hide path dismiss the flyout naturally.
    /// </summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>
    /// Click-only path on the undock/redock button.
    /// The drag path (PreviewMouseLeftButtonDown/Move/Up) sets <see cref="_undockButtonDragOccurred"/>
    /// when motion exceeds <see cref="DragThreshold"/> and finalizes the drag in the button-up handler.
    /// When that happens, the bubbled Click that follows is suppressed here so a press-drag-release
    /// doesn't also flip dock state.
    /// </summary>
    private void UndockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undockButtonDragOccurred)
        {
            _undockButtonDragOccurred = false;
            return;
        }

        if (_isUndocked)
            Redock();
        else
            UndockToSavedPosition();
    }

    private void UndockButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _undockButtonDragOccurred = false;
        // The window may already be at the docked corner (the common case for a press from docked state).
        // BeginDrag seeds IsCurrentlySnapped from the window's current position so a no-motion release
        // still resolves to "redock" rather than "save current position as saved".
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
        // Don't release the capture here, even on the no-drag path: ButtonBase only raises Click
        // from its bubbled OnMouseLeftButtonUp when IsMouseCaptured is still true at that moment,
        // so an early release silently kills the Click and takes the toggle path with it.
        // On the drag path the cursor has moved off the button so ButtonBase's IsMouseOver check
        // skips Click on its own.
        if (!_undockButtonDragOccurred) return;

        if (_dragHelper.IsCurrentlySnapped)
        {
            // Released while parked at the docked corner. Redock without overwriting the previously saved
            // position - a subsequent click of the undock button restores the user's last manual placement.
            IsUndocked = false;
            if (_appSettings != null)
            {
                _appSettings.FlyoutUndocked = false;
                _appSettings.Save();
            }
            Left = _dockedLeft;
            Top = _dockedTop;
        }
        else
            SaveUndockedPosition();

        // _undockButtonDragOccurred is consumed in UndockButton_Click. For a small drag that ends with
        // the cursor still over the button, ButtonBase will still raise Click and we need that flag
        // to short-circuit the toggle path.
    }

    /// <summary>
    /// Drag-to-move when the flyout is undocked.
    /// RootCard is the outermost themed Border, and we listen on its bubbled (non-preview)
    /// MouseLeftButtonDown so interactive children (sliders, buttons) get first refusal, and only
    /// clicks on the empty card surface or row backgrounds reach this handler.
    /// Dragging when docked is intentionally ignored (the docked corner is OS-anchored).
    /// </summary>
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

        if (_dragHelper.IsCurrentlySnapped)
            Redock();
        else
            SaveUndockedPosition();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
