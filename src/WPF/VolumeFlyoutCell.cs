// Uncomment to pad every playback (eRender) device cell with 40 dummy per-app sliders cloned from
// its first real group. Verifies the cell's session-list layout and the flyout's clamp behavior
// when one device's app list overflows the screen on its own.
// Independent of DEBUG_CAPTURE_APP_DUMMIES - both flags can be toggled together or alone.
// #define DEBUG_PLAYBACK_APP_DUMMIES

// Uncomment to pad every capture (eCapture) device cell with 40 dummy per-app sliders cloned from
// its first real group. Verifies the capture-side icon-grid template under the same overflow stress.
// Independent of DEBUG_PLAYBACK_APP_DUMMIES - both flags can be toggled together or alone.
// #define DEBUG_CAPTURE_APP_DUMMIES

// Uncomment to spawn one extra dummy app slider into this cell every time the cell's device volume
// changes (i.e. each PropertyChanged for AudioDevice.Volume - the slider drag-handle, the wheel
// handler, hotkeys, and external mixers all flow through here). Crude stress test: dragging a
// device slider rapidly piles dozens of duplicate app rows into the cell within a single gesture.
// Counter is tracked separately from the rebuild's trim loop so the spawns accumulate across
// CollectionChanged events and only reset when the cell is disposed.
// Independent of the other two debug flags above.
 #define DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Models;

namespace VolumeTrayAppWPF.WPF;

/// <summary>
/// One row in the flyout's device list. Wraps an <see cref="AudioDevice"/> together with a filtered
/// view of its <see cref="AudioDevice.Groups"/> (system sounds, expired groups, and the flyout's own
/// PID are filtered out so the same predicate that the old flat session list applied is preserved
/// per-device).
/// <para/>
/// IsFirst / IsLast are positional flags that the host re-stamps on every cell rebuild so the bottom
/// cell can claim the rounded footer corners through the corner-radius converter.
/// </summary>
internal sealed class VolumeFlyoutCell : INotifyPropertyChanged, IDisposable
{
    private readonly string? _ownAppId;
    private readonly ObservableCollection<AudioAppGroup> _visibleGroups = [];
    private readonly HashSet<AudioAppGroup> _subscribedGroups = [];

    private bool _isFirst;
    private bool _isLast;
    private bool _disposed;

#if DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE
    // Running tally of Volume PropertyChanged hits. Drained as dummy clones inside RebuildVisibleGroups.
    private int _debugVolumeChangeDummies;
#endif

    public AudioDevice Device { get; }
    public ReadOnlyObservableCollection<AudioAppGroup> VisibleGroups { get; }

    /// <summary>True when this cell sits at the top of the device stack inside the flyout.</summary>
    public bool IsFirst
    {
        get => _isFirst;
        set { if (_isFirst != value) { _isFirst = value; OnPropertyChanged(); } }
    }

    /// <summary>True when this cell sits at the bottom of the device stack. Drives the footer-bottom radius.</summary>
    public bool IsLast
    {
        get => _isLast;
        set { if (_isLast != value) { _isLast = value; OnPropertyChanged(); } }
    }

    /// <summary>True when the cell currently has any session rows worth painting; drives the apps-section visibility.</summary>
    public bool HasVisibleGroups => _visibleGroups.Count > 0;

    /// <summary>True for capture-flow devices; XAML uses this to swap to the microphone glyph and label.</summary>
    public bool IsCapture => Device.DataFlow == EDataFlow.eCapture;

    // Rough per-row pixel height of SessionRowTemplate (icon area + the 9px bottom margin). Used
    // only as a multiplier when computing the slider drawer's overflow MaxHeight; matching the
    // actual row height isn't critical because the ScrollViewer clamps content rather than
    // truncates it -- a small under- / overestimate just nudges where the scrollbar appears.
    private const double SliderRowHeightPx = 31.0;

    // Mirror of the GridSlotSize XAML resource (36px). Used by the icon grid's overflow MaxHeight.
    // Hard-coded here rather than pulled from Application.Current.Resources so the cell stays
    // self-contained; if GridSlotSize changes upstream this value must be bumped to match.
    private const double GridSlotSizePx = 36.0;

    /// <summary>
    /// Pixel cap for the slider drawer's ScrollViewer. Resolves the per-device-type setting
    /// (Recording* vs Playback*) by IsCapture and multiplies by the rough slider row height.
    /// Sized so the drawer enters scroll overflow once the visible-group count exceeds the user's
    /// max-apps setting; smaller drawers render at natural height with no scrollbar.
    /// </summary>
    public double SliderDrawerMaxHeight
    {
        get
        {
            AppSettings? s = AppServices.Settings;
            int n = IsCapture
                ? (s?.RecordingAppDrawerSlidersMaxApps ?? 24)
                : (s?.PlaybackAppDrawerSlidersMaxApps ?? 24);
            if (n < 1) n = 1;
            return n * SliderRowHeightPx;
        }
    }

    /// <summary>
    /// Pixel cap for the icon grid drawer's ScrollViewer. Resolves the per-device-type setting by
    /// IsCapture and multiplies by GridSlotSize. In horizontal stack-direction modes the natural
    /// panel height is rows * slot; vertical-flow modes have a fixed perColumn height that is
    /// usually smaller than the cap, so the scrollbar only appears once the user has pushed the
    /// cap below the panel's natural extent.
    /// </summary>
    public double GridDrawerMaxHeight
    {
        get
        {
            AppSettings? s = AppServices.Settings;
            int n = IsCapture
                ? (s?.RecordingAppDrawerIconsMaxRows ?? 10)
                : (s?.PlaybackAppDrawerIconsMaxRows ?? 10);
            if (n < 1) n = 1;
            return n * GridSlotSizePx;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VolumeFlyoutCell(AudioDevice device, string? ownAppId)
    {
        Device = device;
        _ownAppId = ownAppId;
        VisibleGroups = new ReadOnlyObservableCollection<AudioAppGroup>(_visibleGroups);

        ((INotifyCollectionChanged)Device.Groups).CollectionChanged += OnGroupsCollectionChanged;
#if DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE
        Device.PropertyChanged += OnDebugDeviceVolumeChanged;
#endif
        if (AppServices.Settings is { } settings) settings.Changed += OnSettingsChanged;
        RebuildVisibleGroups();
    }

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(SliderDrawerMaxHeight));
        OnPropertyChanged(nameof(GridDrawerMaxHeight));
    }

#if DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE
    private void OnDebugDeviceVolumeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioDevice.Volume)) return;
        _debugVolumeChangeDummies++;
        RebuildVisibleGroups();
    }
#endif

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildVisibleGroups();

    /// <summary>
    /// Invoked from the host when a watched group raises <see cref="AudioAppGroup.State"/>. Filter
    /// inclusion can flip on state transitions (Expired in particular), so we rerun the filter.
    /// </summary>
    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AudioAppGroup.State)) return;
        RebuildVisibleGroups();
    }

    /// <summary>
    /// Rebuilds the filtered list. Predicate matches the original flat-list filter: skip Expired
    /// groups, system sounds, empty groups, and the flyout's own audio session (the per-app feedback
    /// wav routes through SoundPlayer, which would otherwise show up as our own slider transiently).
    /// </summary>
    private void RebuildVisibleGroups()
    {
        if (_disposed) return;

        bool changed = false;
        int writeIndex = 0;

        foreach (AudioAppGroup g in Device.Groups)
        {
            if (!IsGroupVisible(g)) continue;

            // Newly-seen groups need a State subscription so a later Expired transition removes them.
            if (_subscribedGroups.Add(g)) g.PropertyChanged += OnGroupPropertyChanged;

            if (writeIndex >= _visibleGroups.Count)
            {
                _visibleGroups.Add(g);
                changed = true;
            }
            else if (!ReferenceEquals(_visibleGroups[writeIndex], g))
            {
                // Out-of-order entry: replace in place so binding updates land in one notification.
                _visibleGroups[writeIndex] = g;
                changed = true;
            }
            writeIndex++;
        }

        // Drop trailing entries that aren't in the live list anymore.
        while (_visibleGroups.Count > writeIndex)
        {
            _visibleGroups.RemoveAt(_visibleGroups.Count - 1);
            changed = true;
        }

#if DEBUG_PLAYBACK_APP_DUMMIES
        // Pad playback cells with 40 dummy app sliders by repeating the first real group reference.
        // Same AudioAppGroup added N times - piggybacks the existing subscription; writes to a dummy
        // slider still fan out to the real session. Trailing dummies are wiped by the trim loop above
        // on the next rebuild, so the count stays stable across CollectionChanged events.
        if (!IsCapture && _visibleGroups.Count > 0)
        {
            AudioAppGroup template = _visibleGroups[0];
            for (int dummyIndex = 0; dummyIndex < 40; dummyIndex++)
                _visibleGroups.Add(template);
            changed = true;
        }
#endif

#if DEBUG_CAPTURE_APP_DUMMIES
        // Pad capture cells with 40 dummy app sliders by repeating the first real group reference.
        // Same trick as the playback variant - the icon-grid template binds to each slot independently
        // so a single AudioAppGroup repeated N times just produces N visual rows.
        if (IsCapture && _visibleGroups.Count > 0)
        {
            AudioAppGroup template = _visibleGroups[0];
            for (int dummyIndex = 0; dummyIndex < 40; dummyIndex++)
                _visibleGroups.Add(template);
            changed = true;
        }
#endif

#if DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE
        // Drain the volume-change counter into duplicate refs of the first real group. The trim loop
        // above wipes whatever we added last time, so re-appending the full count here is what makes
        // the dummies appear monotonic across rebuilds.
        if (_debugVolumeChangeDummies > 0 && _visibleGroups.Count > 0)
        {
            AudioAppGroup template = _visibleGroups[0];
            for (int dummyIndex = 0; dummyIndex < _debugVolumeChangeDummies; dummyIndex++)
                _visibleGroups.Add(template);
            changed = true;
        }
#endif

        // Unsubscribe from groups that left the device entirely so we don't pin them.
        AudioAppGroup[] subscribed = _subscribedGroups.ToArray();
        for (int i = 0; i < subscribed.Length; i++)
        {
            AudioAppGroup g = subscribed[i];
            bool stillOnDevice = false;
            foreach (AudioAppGroup live in Device.Groups)
            {
                if (ReferenceEquals(live, g)) { stillOnDevice = true; break; }
            }
            if (!stillOnDevice)
            {
                g.PropertyChanged -= OnGroupPropertyChanged;
                _subscribedGroups.Remove(g);
            }
        }

        if (changed) OnPropertyChanged(nameof(HasVisibleGroups));
    }

    private bool IsGroupVisible(AudioAppGroup g)
    {
        if (g.State == AudioSessionState.Expired) return false;
        if (g.IsSystemSounds) return false;
        if (g.Sessions.Count == 0) return false;
        if (_ownAppId != null && string.Equals(g.AppId, _ownAppId, StringComparison.Ordinal)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ((INotifyCollectionChanged)Device.Groups).CollectionChanged -= OnGroupsCollectionChanged;
#if DEBUG_SPAWN_APP_DUMMY_ON_DEVICE_VOLUME_CHANGE
        Device.PropertyChanged -= OnDebugDeviceVolumeChanged;
#endif
        if (AppServices.Settings is { } settings) settings.Changed -= OnSettingsChanged;

        foreach (AudioAppGroup g in _subscribedGroups) g.PropertyChanged -= OnGroupPropertyChanged;
        _subscribedGroups.Clear();
        _visibleGroups.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
