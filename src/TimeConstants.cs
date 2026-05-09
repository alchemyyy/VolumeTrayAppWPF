namespace VolumeTrayAppWPF;

// Central registry of hardcoded time values used across the app. Anything that
// is genuinely user-configurable lives on AppSettings instead -- this file is
// for fixed constants only. All values are in milliseconds; call sites wrap
// with TimeSpan.FromMilliseconds(...) when the consuming API requires TimeSpan.
public static class TimeConstants
{
    // Crash & shutdown drain
    public const int CrashHandlerDrainTimeoutMs = 500;
    public const int ProcessExitDrainTimeoutMs = 200;
    public const int SessionEndingDrainTimeoutMs = 2_000;
    public const int NormalShutdownDrainTimeoutMs = 3_000;
    public const int DrainAdditionalMarginMs = 250;
    public const int DrainPollIntervalMs = 50;

    // Crash recovery & watcher
    public const int CrashRestartDelayMs = 1_000;
    public const int RapidRestartDetectionWindowMs = 30_000;
    public const int WatcherLivenessPollIntervalMs = 1_000;

    // Single instance
    public const int SingleInstanceMutexAcquireTimeoutMs = 5_000;

    // Tray / Shell
    public const int TaskbarRecreateCheckIntervalMs = 500;

    // Settings UI
    public const int SettingsDragAnimationDurationMs = 150;
    public const int PostSettingsCloseGCDelayMs = 10_000;

    // Color picker
    public const int ColorPickerChangeCooldownMs = 50;

    // Tray icon update throttle default; the host app may override per instance.
    public const int TrayIconUpdateRateDefaultMs = 50;

    // Volume slider -> COM write throttle. AsyncThrottler coalesces drag events into a single
    // SetMasterVolume(Level)Scalar call per cooldown so the audio driver isn't hammered.
    // 30ms ~= 33Hz, smooth for a slider drag without flooding WASAPI on rapid mouse movement.
    public const int VolumeWriteRateDefaultMs = 30;

    // Default-device refresh coalescing dwell. A single device disable / default-change can fire
    // up to four IMMNotificationClient callbacks (Console / Multimedia / Communications role
    // transitions plus the state change itself); dwelling this long inside the AsyncThrottler
    // payload before doing the work, then bailing on HasReplacement, collapses the burst into a
    // single UpdateAllDefaults pass. 50ms is short enough to feel instant and long enough to
    // catch the trailing role-change notifications.
    public const int DefaultsRefreshCoalesceDwellMs = 50;

    // Logging
    // 7 days in ms = 7 * 24 * 60 * 60 * 1000 = 604_800_000.
    public const int LogMaxAgeMs = 604_800_000;
    public const int LogFlushIntervalMs = 2_000;
    public const int LogShutdownTimerWaitMs = 1_000;
}
