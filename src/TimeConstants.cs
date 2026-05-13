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

    // Trailing-edge debounce window for the volume-change ding. Each scroll/wheel event resets this
    // timer; the ding only fires once the timer elapses with no fresh event arriving. Keeps a fast
    // wheel spin (or rapid slider drag releases) from machine-gunning the beep. long enough
    // to cover a normal scroll cadence and short enough that the ding still feels coupled to the gesture.
    public const int VolumeFeedbackDingDelayMs = 350;

    // Logging
    // 7 days in ms = 7 * 24 * 60 * 60 * 1000 = 604_800_000.
    public const int LogMaxAgeMs = 604_800_000;
    public const int LogFlushIntervalMs = 2_000;
    public const int LogShutdownTimerWaitMs = 1_000;

    // Stuck peak-meter watchdog dwell. Windows occasionally latches IAudioMeterInformation on
    // idle render endpoints (Bluetooth A2DP offload is the common offender) so the COM call keeps
    // returning the exact same non-zero peak pair forever. Each fresh pair re-arms this one-shot
    // timer; if no different pair arrives within the window, the callback marks the meter latched
    // and subsequent same-value samples force the lerp to silence so the bar decays instead of
    // freezing on the latched value. 1s is well past any sustained audio frame's bit-level wiggle
    // and short enough that a real freeze visibly resolves within "blink and you'll miss it".
    public const int MeterStaleWatchdogMs = 1_000;

    // Bluetooth battery active-poll interval. The PnP watcher emits Updated events on
    // Connected-state changes but not on battery deltas, so without an explicit re-query via
    // CM_Get_DevNode_Property the bound UI would freeze on the value read at Added time. The
    // timer is only running while the flyout is open (no point polling the OS when nothing is
    // bound). 30s is well under typical headset reporting cadence and matches what Windows
    // Settings itself polls at.
    public const int BluetoothBatteryPollIntervalMs = 30_000;

    // Endpoint-render drain poll slice.
    // Used by Audio/EndpointSoundPlayback for the post-write padding-poll loop inside Play().
    public const int EndpointSoundPlaybackPollSliceMs = 30;

    // Hard ceiling on the endpoint-render drain loop.
    // Default Windows feedback wavs are well under a second; this covers worst-case engine latency
    // on a slow / contested system without ever stranding a worker.
    public const int EndpointSoundPlaybackMaxDrainMs = 5_000;

    // Auto-update
    // Default cadence the background UpdateCheckService polls GitHub at. 1 hour is a low-traffic compromise:
    // recent enough to surface a fresh release the same workday, infrequent enough to stay well clear of
    // GitHub's unauthenticated 60/hr rate limit even across the per-IP shared quota.
    public const int UpdateCheckIntervalDefaultMs = 3_600_000;
    public const int UpdateCheckIntervalMinMs = 60_000;
    public const int UpdateCheckIntervalMaxMs = 86_400_000;
    // Extra grace beyond the configured interval before the UI flips "Install update" to "Version stale".
    public const int UpdateStaleGraceMs = 5_000;
    // Per-request HTTP timeout for both the release-metadata GET and the asset download GET.
    public const int UpdateNetworkTimeoutMs = 30_000;
    // Short delay before kicking the very first check on startup so it doesn't compete with the
    // audio device manager init and the flyout pre-warm for the first few seconds of process life.
    public const int UpdateCheckStartupDelayMs = 5_000;
}
