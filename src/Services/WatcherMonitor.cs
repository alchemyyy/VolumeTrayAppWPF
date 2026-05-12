using System.Diagnostics;
using System.Windows.Threading;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Services;

/// <summary>
/// Polls the watcher process (the parent that supervises the monitored child) and signals the host
/// to exit when the watcher dies, so we don't run orphaned. Wraps a Task.Run polling loop on a
/// CancellationTokenSource so Dispose stops the loop cleanly.
/// Inert when no watcher PID was passed on the command line.
/// </summary>
public sealed class WatcherMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _onWatcherDied;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Construct the monitor. <paramref name="onWatcherDied"/> is invoked on
    /// <paramref name="dispatcher"/> when the watcher process is observed to have exited.
    /// </summary>
    public WatcherMonitor(Dispatcher dispatcher, Action onWatcherDied)
    {
        _dispatcher = dispatcher;
        _onWatcherDied = onWatcherDied;
    }

    /// <summary>
    /// Begin polling the watcher process. No-op when <see cref="Program.WatcherPID"/> is null
    /// (the app was started without --watcher-pid). Idempotent for a single live instance.
    /// </summary>
    public void Start()
    {
        if (Program.WatcherPID is not { } watcherPID) return;
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using Process watcherProcess = Process.GetProcessById(watcherPID);

                while (!token.IsCancellationRequested)
                {
                    if (watcherProcess.HasExited)
                    {
                        await _dispatcher.InvokeAsync(_onWatcherDied);
                        return;
                    }

                    await Task.Delay(TimeConstants.WatcherLivenessPollIntervalMs, token);
                }
            }
            catch (ArgumentException)
            {
                // Watcher PID already gone - exit immediately.
                await _dispatcher.InvokeAsync(_onWatcherDied);
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation during shutdown.
            }
            catch (Exception ex)
            {
                WPFLog.Log($"WatcherMonitor poll loop: {ex.Message}");
            }
        }, token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cts != null)
        {
            try { _cts.Cancel(); }
            catch (Exception ex) { WPFLog.Log($"WatcherMonitor.Dispose: cancel: {ex.Message}"); }
            Safe.Dispose(_cts);
            _cts = null;
        }
    }
}
