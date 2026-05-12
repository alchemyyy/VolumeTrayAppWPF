using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

// Aggregates every audio session that shares an AppID into a single slider, mirroring EarTrumpet's
// AudioDeviceSessionGroup. Discord, Chromium-based browsers, and Electron apps spawn several child
// processes that each register their own IAudioSessionControl with WASAPI; without grouping, the
// flyout would show two or three sliders for one app.
//
// The group exposes the same bindable surface area as a single AudioSession (DisplayName, Icon,
// Volume, IsMuted, PeakValue, State) so the flyout DataTemplate can bind to either type. Volume
// and IsMuted writes fan out to every session in the group; reads return the first session's value.
// PeakValue is the max across all sessions so the loudest stream drives the meter.
internal sealed class AudioAppGroup(string appID, Dispatcher dispatcher) : INotifyPropertyChanged, IDisposable
{
    private readonly List<AudioSession> _sessions = [];
    private readonly Dispatcher _dispatcher = dispatcher;

    private float _peakValueMin;
    private float _peakValueMax;
    private bool _isExclusiveControlHolder;
    private bool _disposed;

    public string AppID { get; } = appID;

    /// <summary>The sessions inside this group. Mutated only on the UI thread by AudioDevice.</summary>
    public IReadOnlyList<AudioSession> Sessions => _sessions;

    public string DisplayName => _sessions.Count > 0 ? _sessions[0].DisplayName : "Unknown";
    public ImageSource? Icon => _sessions.Count > 0 ? _sessions[0].Icon : null;
    public bool IsSystemSounds => _sessions.Count > 0 && _sessions[0].IsSystemSounds;
    public uint ProcessID => _sessions.Count > 0 ? _sessions[0].ProcessID : 0;

    // Tooltip surface for the per-app icon. Computed (rather than MultiBinding in XAML) so the
    // binding stays a plain Path="TooltipText" - matches the rest of the bindable surface and
    // avoids quirks WPF has resolving MultiBindings against internal types.
    public string TooltipText => _sessions.Count > 0
        ? $"{_sessions[0].DisplayName}\nPID: {_sessions[0].ProcessID}"
        : "Unknown";

    /// <summary>Active if any session in the group is active; expired only when every session has expired.</summary>
    public AudioSessionState State
    {
        get
        {
            bool isInactive = false;
            foreach (AudioSession session in _sessions)
            {
                if (session.State == AudioSessionState.Active)
                    return AudioSessionState.Active;
                if (session.State != AudioSessionState.Expired)
                    isInactive = true;
            }
            return isInactive ? AudioSessionState.Inactive : AudioSessionState.Expired;
        }
    }

    public float Volume
    {
        get => _sessions.Count > 0 ? _sessions[0].Volume : 0f;
        set
        {
            // Fan out to every session. Each AudioSession.Volume.set already filters near-equal writes
            // and tolerates COM failures, so no extra guard is needed here.
            foreach (AudioSession session in _sessions)
                session.Volume = value;

            OnPropertyChanged();
        }
    }

    public bool IsMuted
    {
        get => _sessions.Count > 0 && _sessions[0].IsMuted;
        set
        {
            foreach (AudioSession session in _sessions)
                session.IsMuted = value;

            OnPropertyChanged();
        }
    }

    /// <summary>Loudest min(L, R) peak across the grouped sessions; drives the base meter bar.</summary>
    public float PeakValueMin
    {
        get => _peakValueMin;
        private set { if (Math.Abs(value - _peakValueMin) > 0.001f) { _peakValueMin = value; OnPropertyChanged(); } }
    }

    /// <summary>Loudest max(L, R) peak across the grouped sessions; drives the stereo overlay.</summary>
    public float PeakValueMax
    {
        get => _peakValueMax;
        private set { if (Math.Abs(value - _peakValueMax) > 0.001f) { _peakValueMax = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// True when one of this group's sessions is the process currently holding the parent device
    /// in exclusive mode. Drives the mini-glyph lock overlay on the app icon. Backend stub:
    /// AudioDevice pushes this true when its <see cref="AudioDevice.ExclusiveControlHolderPID"/>
    /// matches any session's PID. Until that detection lands the flag stays false.
    /// </summary>
    public bool IsExclusiveControlHolder
    {
        get => _isExclusiveControlHolder;
        internal set { if (_isExclusiveControlHolder != value) { _isExclusiveControlHolder = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the last session is removed so AudioDevice can drop the group from its list.</summary>
    internal event Action<AudioAppGroup>? Empty;

    /// <summary>
    /// Adds a session to the group. New sessions inherit the group's existing mute state so a freshly
    /// spawned Discord renderer doesn't unmute an app that the user had silenced.
    /// </summary>
    public void AddSession(AudioSession session)
    {
        if (_sessions.Count > 0)
        {
            // Inherit current mute state. AudioSession.IsMuted.set guards against echo and COM failure
            // internally, so a best-effort write is safe.
            try { session.IsMuted = _sessions[0].IsMuted || session.IsMuted; }
            catch { /* session may already be torn down */ }
        }

        _sessions.Add(session);
        session.PropertyChanged += OnSessionPropertyChanged;

        // First session populates the bindable surface; subsequent ones don't change the
        // representative-derived properties so re-emitting them on every add wastes binding work.
        if (_sessions.Count == 1)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(IsMuted));
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(ProcessID));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    /// <summary>
    /// Removes a session from the group. Raises <see cref="Empty"/> when the last session leaves so
    /// the device can prune the group; otherwise re-emits property change for any representative-derived
    /// fields since the head session may have shifted.
    /// </summary>
    public void RemoveSession(AudioSession session)
    {
        if (!_sessions.Remove(session)) return;
        session.PropertyChanged -= OnSessionPropertyChanged;

        if (_sessions.Count == 0)
        {
            Empty?.Invoke(this);
            return;
        }

        // Representative session (index 0) may have changed; refresh anything keyed off it.
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(IsMuted));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ProcessID));
        OnPropertyChanged(nameof(TooltipText));
        RefreshAggregatePeak();
    }

    /// <summary>
    /// Bg-thread fan-out. Forwards the COM-read into every session so per-session raw peaks
    /// are populated in parallel off the UI thread. Snapshots the session list under try/catch
    /// because UI-thread Add/RemoveSession could otherwise tear the enumerator; a torn frame
    /// just means we miss one 33 ms tick for the affected group.
    /// The (unified, biasMultiplier) pair flows down unchanged so per-session bars collapse
    /// in lockstep with the device bar when unified mode is on.
    /// </summary>
    internal void UpdatePeakValueBackground(bool unified, int biasMultiplier)
    {
        if (_disposed) return;

        AudioSession[] sessions;
        try { sessions = _sessions.ToArray(); }
        catch { return; }

        foreach (AudioSession session in sessions)
        {
            try { session.UpdatePeakValueBackground(unified, biasMultiplier); }
            catch { /* session may have died between callbacks */ }
        }
    }

    /// <summary>
    /// Sample-timer fan-out (UI thread). Forwards into every session so each session arms its
    /// own lerp from the latest cached raw peak. The group's own <see cref="PeakValue"/> doesn't
    /// interpolate - it just maxes over the sessions, which are already smoothed individually.
    /// </summary>
    internal void OnNewSample(int interpolationSteps)
    {
        if (_disposed) return;
        for (int i = _sessions.Count - 1; i >= 0; i--) _sessions[i].OnNewSample(interpolationSteps);
    }

    /// <summary>
    /// Render-timer fan-out. Each session's render tick fires PeakValue PropertyChanged when it
    /// shifts, which OnSessionPropertyChanged observes to recompute the group max - so this single
    /// pass is enough to keep both per-session and per-group meters smooth.
    /// </summary>
    internal void OnRenderTick()
    {
        if (_disposed) return;
        for (int i = _sessions.Count - 1; i >= 0; i--) _sessions[i].OnRenderTick();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Re-emit representative-derived properties so the UI rebinds when an underlying session
        // mutates (volume changed via Windows Volume Mixer, icon path updated by Discord, etc.).
        switch (e.PropertyName)
        {
            case nameof(AudioSession.Volume):
                if (ReferenceEquals(sender, _sessions.Count > 0 ? _sessions[0] : null))
                    OnPropertyChanged(nameof(Volume));
                break;
            case nameof(AudioSession.IsMuted):
                if (ReferenceEquals(sender, _sessions.Count > 0 ? _sessions[0] : null))
                    OnPropertyChanged(nameof(IsMuted));
                break;
            case nameof(AudioSession.PeakValueMin):
            case nameof(AudioSession.PeakValueMax):
                RefreshAggregatePeak();
                break;
            case nameof(AudioSession.Icon):
                if (ReferenceEquals(sender, _sessions.Count > 0 ? _sessions[0] : null))
                    OnPropertyChanged(nameof(Icon));
                break;
            case nameof(AudioSession.DisplayName):
                if (ReferenceEquals(sender, _sessions.Count > 0 ? _sessions[0] : null))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(TooltipText));
                }
                break;
            case nameof(AudioSession.State):
                OnPropertyChanged(nameof(State));
                break;
        }
    }

    private void RefreshAggregatePeak()
    {
        float maxOfMins = 0f;
        float maxOfMaxes = 0f;
        foreach (AudioSession session in _sessions)
        {
            float pMin = session.PeakValueMin;
            float pMax = session.PeakValueMax;
            if (pMin > maxOfMins) maxOfMins = pMin;
            if (pMax > maxOfMaxes) maxOfMaxes = pMax;
        }
        PeakValueMin = maxOfMins;
        PeakValueMax = maxOfMaxes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (AudioSession session in _sessions)
            session.PropertyChanged -= OnSessionPropertyChanged;

        _sessions.Clear();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
