using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Diagnostic ETW spike for HFP (Hands-Free Profile) codec discovery.
/// <para/>
/// Microsoft ships no manifested HFP equivalent of <c>Microsoft.Windows.Bluetooth.BthA2dp</c>,
/// but two undocumented TraceLogging providers live inside the HFP driver binaries
/// (<c>BthHfAud.sys</c> and <c>BthHfEnum.sys</c>) and register cleanly with the same elevation
/// posture BthA2dp uses. The provider GUIDs below were extracted by reading the
/// TRACELOGGING_DEFINE_PROVIDER macro's embedded identity from the driver image - the same
/// technique the BluetoothAudioCodecInspector reference repo verified against BthA2dp.
/// <para/>
/// Spike mode: subscribe to every dynamic event arriving from either provider, log the full
/// payload schema on the first occurrence of each (provider, event-name) pair, and a compact
/// payload-values line on each subsequent occurrence. The first-fire dump tells us which event
/// carries the codec / sample-rate byte; the compact follow-ups show how the value transitions
/// across CVSD &lt;-&gt; mSBC. Once the right event + field is identified, this class should be
/// folded into <see cref="BluetoothCodecMonitor"/> (or a sibling) that fires
/// <c>CodecChanged</c> for HFP, and the verbose logging here should be removed.
/// <para/>
/// Same elevation / threading shape as <see cref="BluetoothCodecMonitor"/>: requires Administrator
/// or Performance Log Users to open the ETW session; the worker thread fires the callback and we
/// stay on it (no Dispatcher hop) - this is logging-only, no UI binding state to mutate.
/// </summary>
internal sealed class HfpCodecMonitor : INotifyPropertyChanged, IDisposable
{
    // Stable session name so a prior crashed run can be detected and stopped before we open ours.
    private const string SessionName = "VolumeTrayAppWPF-HfpSpike";

    // Microsoft.Windows.Bluetooth.HfAud - extracted from C:\Windows\System32\drivers\BthHfAud.sys
    // (the HFP audio class driver, direct analogue of BthA2dp.sys). Strongest candidate events
    // from the binary's TraceLogging metadata: HfpPinCreate, HfpPinSetDataFormat,
    // HfpPinSetDeviceState. HfpPinSetDataFormat is expected to carry the negotiated PCM sample
    // rate that pins down CVSD (8 kHz) vs mSBC (16 kHz).
    private static readonly Guid HfAudProviderGuid = new("8d87aa29-0ebe-41af-b3c4-f56ce668bd22");

    // Microsoft.Windows.Bluetooth.HfEnum - extracted from BthHfEnum.sys (the HFP bus enumerator
    // that manages SCO setup). Candidate events: HfpRoleConfigurationSet, ScoConnection,
    // ScoDisconnection, HfpScoInStreamStats, HfpScoOutStreamStats. The role-configuration or
    // SCO-connection event is the most likely codec carrier on this provider.
    private static readonly Guid HfEnumProviderGuid = new("9680893f-0696-4ec0-9af5-65974897c9d4");

    private readonly Dispatcher _dispatcher;
    private TraceEventSession? _session;
    private Thread? _worker;
    private bool _isRunning;
    private bool _requiresElevation;
    private bool _disposed;

    // (provider, event) pairs we've already dumped a full schema for. Keeps the log readable -
    // each event type contributes one verbose line on first fire and one compact line per
    // subsequent fire. ConcurrentBag would be overkill; the callback is single-threaded inside
    // the worker, so a plain HashSet is fine.
    private readonly HashSet<string> _seenEventKeys = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    public HfpCodecMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>True once the ETW session is open and the worker is pumping events.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// True when <see cref="Start"/> was called by a non-elevated process. The session never
    /// opened; the spike contributes nothing to the log in that state.
    /// </summary>
    public bool RequiresElevation
    {
        get => _requiresElevation;
        private set { if (_requiresElevation != value) { _requiresElevation = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Opens a realtime ETW session and subscribes to every event from both HFP providers.
    /// Idempotent. Failures are logged and leave the spike inert without throwing.
    /// </summary>
    public void Start()
    {
        if (_disposed || _isRunning) return;

        if (TraceEventSession.IsElevated() != true)
        {
            RequiresElevation = true;
            WPFLog.Log("HfpCodecMonitor.Start: requires admin / Performance Log Users; staying inert.");
            return;
        }

        try
        {
            StopOrphanedSession();

            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create)
            {
                StopOnDispose = true,
            };

            // Dynamic.All catches every event from any provider enabled on this session - which
            // is exactly what spike mode wants. We don't know the event names ahead of time, so
            // per-event subscription via AddCallbackForProviderEvent doesn't work.
            _session.Source.Dynamic.All += OnDynamicEvent;

            _session.EnableProvider(HfAudProviderGuid, TraceEventLevel.Verbose, 0);
            _session.EnableProvider(HfEnumProviderGuid, TraceEventLevel.Verbose, 0);

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "HfpCodecMonitor.ETW",
            };
            _worker.Start();

            IsRunning = true;
            WPFLog.Log("HfpCodecMonitor.Start: session opened, watching HfAud {8d87aa29-...} + HfEnum {9680893f-...}");
        }
        catch (Exception ex)
        {
            WPFLog.Log($"HfpCodecMonitor.Start: failed: {ex.Message}");
            Safe.Dispose(_session);
            _session = null;
        }
    }

    // Defensive cleanup of a stale session from a previous crashed run. ETW sessions outlive the
    // creating process (they live in the kernel until explicitly stopped); reopening with the
    // same name fails with ALREADY_EXISTS otherwise. Mirrors BluetoothCodecMonitor's pattern.
    private static void StopOrphanedSession()
    {
        try
        {
            foreach (string name in TraceEventSession.GetActiveSessionNames())
            {
                if (!string.Equals(name, SessionName, StringComparison.Ordinal)) continue;
                try
                {
                    using TraceEventSession existing = new(name, TraceEventSessionOptions.Attach);
                    existing.Stop();
                    WPFLog.Log("HfpCodecMonitor: stopped orphaned ETW session " + name);
                }
                catch (Exception ex)
                {
                    WPFLog.Log($"HfpCodecMonitor: failed to stop orphan {name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"HfpCodecMonitor: enumerate orphans failed: {ex.Message}");
        }
    }

    private void WorkerLoop()
    {
        try
        {
            // Source.Process blocks until the session is disposed. Per-event exceptions are
            // swallowed inside OnDynamicEvent so a single bad event can't tear the pump down.
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            WPFLog.Log($"HfpCodecMonitor.WorkerLoop: {ex.Message}");
        }
    }

    private void OnDynamicEvent(TraceEvent evt)
    {
        try
        {
            string providerName = evt.ProviderName ?? "<unknown-provider>";
            string eventName = evt.EventName ?? "<unknown-event>";
            string key = providerName + ":" + eventName;

            if (_seenEventKeys.Add(key))
            {
                LogEvent(evt, providerName, eventName, "FIRST");
            }
            else
            {
                LogEvent(evt, providerName, eventName, "TICK");
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"HfpCodecMonitor.OnDynamicEvent: {ex.Message}");
        }
    }

    // Dumps the event's full payload (name + value pairs) as one log line. The first occurrence
    // is tagged FIRST so the user can grep for the schema-discovery moments; subsequent fires
    // are tagged TICK and share the same shape so deltas (e.g. SampleRate flipping 8000 -> 16000
    // when WBS toggles) are easy to eyeball in the log. Debug-only: this spike is for development
    // diagnosis, stripped in Release so production logs aren't drowned in per-event payload dumps.
    [Conditional("DEBUG")]
    private static void LogEvent(TraceEvent evt, string providerName, string eventName, string tag)
    {
        try
        {
            StringBuilder sb = new();
            sb.Append("HfpSpike[").Append(tag).Append("] ")
              .Append(providerName).Append('.').Append(eventName)
              .Append(" task=").Append(evt.TaskName)
              .Append(" opcode=").Append(evt.OpcodeName)
              .Append(" id=").Append((int)evt.ID)
              .Append(" level=").Append(evt.Level)
              .Append(" payload={");

            string[] names = evt.PayloadNames;
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                object? value;
                try { value = evt.PayloadValue(i); }
                catch (Exception ex) { value = "<unreadable: " + ex.GetType().Name + ">"; }
                sb.Append('[').Append(i).Append(']')
                  .Append(names[i] ?? "?").Append('=')
                  .Append(value ?? "<null>");
            }
            sb.Append('}');
            WPFLog.LogDebug(sb.ToString());
        }
        catch (Exception ex)
        {
            WPFLog.LogDebug($"HfpSpike: LogEvent failed: {ex.Message}");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;

        // Disposing the session causes Source.Process to return on the worker. Capture the
        // worker reference before the field is nulled so a racing setter can't swap a new
        // thread in mid-shutdown.
        Thread? worker = _worker;
        Safe.Dispose(_session);
        _session = null;
        _worker = null;

        if (worker != null)
        {
            try { worker.Join(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { WPFLog.Log($"HfpCodecMonitor.Dispose: join: {ex.Message}"); }
        }
    }
}
