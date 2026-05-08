namespace VolumeTrayAppWPF;

/// <summary>
/// Immutable app identity used for path- and name-agnostic single-instance detection.
/// Keyed by a fixed GUID so renaming or moving the .exe cannot defeat it.
/// Distinct from the tray-icon GUID in ShellNotifyIcon.
/// </summary>
internal static class AppIdentity
{
    public const string AppGuid = "1ac1ef49-bb6a-4a21-8480-24766db1f35e";

    public static string SingleInstanceMutexName => $"Local\\VolumeTrayAppWPF-Watcher-{AppGuid}";
    public static string PIDMmfName              => $"Local\\VolumeTrayAppWPF-WatcherPID-{AppGuid}";

    // MMF layout (12 bytes): generation, watcher PID, monitored PID.
    // Generation == 0 means "no valid owner yet" (e.g. crashed mid-write).
    public const int MmfSize = 12;
    public const int OffsetGeneration   = 0;
    public const int OffsetWatcherPID   = 4;
    public const int OffsetMonitoredPID = 8;
}
