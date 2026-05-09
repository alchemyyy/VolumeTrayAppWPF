using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VolumeTrayAppWPF.Audio;
using VolumeTrayAppWPF.Audio.Interop;

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
    private readonly HashSet<AudioAppGroup> _subscribedGroups = new();

    private bool _isFirst;
    private bool _isLast;
    private bool _disposed;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public VolumeFlyoutCell(AudioDevice device, string? ownAppId)
    {
        Device = device;
        _ownAppId = ownAppId;
        VisibleGroups = new ReadOnlyObservableCollection<AudioAppGroup>(_visibleGroups);

        ((INotifyCollectionChanged)Device.Groups).CollectionChanged += OnGroupsCollectionChanged;
        RebuildVisibleGroups();
    }

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

        foreach (AudioAppGroup g in _subscribedGroups) g.PropertyChanged -= OnGroupPropertyChanged;
        _subscribedGroups.Clear();
        _visibleGroups.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
