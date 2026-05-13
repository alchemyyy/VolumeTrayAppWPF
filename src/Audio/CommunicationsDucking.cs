using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Audio;

/// <summary>
/// Integer values written to UserDuckingPreference. Names mirror the mmsys.cpl Communications
/// tab radio buttons; numeric values match the registry encoding so an enum cast equals a write.
/// </summary>
internal enum CommunicationsDuckingMode
{
    MuteAll = 0,
    Reduce80 = 1,
    Reduce50 = 2,
    DoNothing = 3,
}

/// <summary>
/// System-wide "When Windows detects communications activity" preference (mmsys.cpl Communications
/// tab). Three surfaces:
///   IsActive()    Read the current preference. True for any active ducking mode (mute / 80% /
///                 50%); false only for "Do nothing". Pure read - safe to call any time.
///   SetMode()     Write the preference. The watcher (when running) picks it up and fires Changed.
///   Changed       Fires on the watcher thread when any value under the Multimedia\Audio key is
///                 written. Listeners marshal to the UI thread and re-read IsActive().
///
/// Watcher lifecycle is explicit Start / Stop. The flyout starts on Show and stops on Hide so the
/// thread, key handle, and notification event only exist while the indicator can actually be seen.
///
/// There is no documented Win32 / COM API for this setting - mmsys.cpl writes the registry directly
/// and the audio service reads it. Per-session IAudioSessionControl2::SetDuckingPreference in
/// AudioPolicy.cs is a different surface (per-app opt-out, not the global policy).
///
/// Storage:
///   HKCU\Software\Microsoft\Multimedia\Audio\UserDuckingPreference   REG_DWORD
///
/// Value mapping (verified against Microsoft Q&A answers; mmsys.cpl writes these integers):
///   0  Mute all other sounds
///   1  Reduce the volume of other sounds by 80%   (OS default; usually the missing-value state)
///   2  Reduce the volume of other sounds by 50%
///   3  Do nothing
///
/// Missing key or value falls back to the OS default (Reduce 80%, which is active).
///
/// Watcher shape mirrors ProcessExitMonitor: WaitForMultipleObjects blocks on a wake event (slot 0,
/// auto-reset) and the registry notification event (slot 1, manual-reset). Stop signals the wake
/// event; the loop sees it at slot 0 and unwinds, closing the key and both events.
/// </summary>
internal static class CommunicationsDucking
{
    private const string KeyPath = @"Software\Microsoft\Multimedia\Audio";
    private const string ValueName = "UserDuckingPreference";

    private const int DoNothingMode = (int)CommunicationsDuckingMode.DoNothing;

    // HKEY_CURRENT_USER. Stable Win32 predefined pseudo-handle (top bit set).
    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));

    // Access rights for RegOpenKeyEx. KEY_NOTIFY is the only right RegNotifyChangeKeyValue needs;
    // KEY_QUERY_VALUE is folded in defensively so the same handle could also serve direct reads.
    private const int KEY_NOTIFY = 0x0010;
    private const int KEY_QUERY_VALUE = 0x0001;

    // RegNotifyChangeKeyValue filter. REG_NOTIFY_CHANGE_LAST_SET fires on any value write under
    // the watched key - subkey / name / attribute changes don't concern us.
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    private const int ERROR_SUCCESS = 0;

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegNotifyChangeKeyValue(IntPtr hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

    /// <summary>
    /// Raised on the background watcher thread when the Multimedia\Audio key reports a value
    /// write. Listeners must marshal to the UI thread and re-read <see cref="IsActive"/>. No
    /// payload - we don't track which value flipped; any write under the key is just a hint to
    /// re-poll.
    /// </summary>
    public static event Action? Changed;

    // Watcher state. All access goes through Gate so Start / Stop / WatchLoop never observe a
    // half-initialized snapshot.
    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static IntPtr _hKey;
    private static IntPtr _hRegEvent;
    private static IntPtr _hWakeEvent;

    /// <summary>
    /// True for any active ducking mode (mute / 80% / 50%); false only when the user has
    /// explicitly picked "Do nothing". Missing key or value counts as active (OS default is
    /// Reduce 80%). Pure registry read - does not start the watcher.
    /// </summary>
    public static bool IsActive()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key == null) return true;
        if (key.GetValue(ValueName) is not int v) return true;
        return v != DoNothingMode;
    }

    /// <summary>
    /// Writes the preference. The watcher picks up the change on its own and raises
    /// <see cref="Changed"/>, so callers don't need to refresh anything by hand. Per MS Q&amp;A
    /// the audio service may need an mmsys.cpl Apply (or a sign-out) to honor the new value at
    /// the next ducking event - the registry / visual updates immediately regardless.
    /// </summary>
    public static void SetMode(CommunicationsDuckingMode mode)
    {
        // CreateSubKey opens-or-creates with write access; the Multimedia\Audio key already
        // exists on every Windows install we care about, but Create is the safer call shape.
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, (int)mode, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Opens the watched key, creates the wake / notification events, and spins up the wait
    /// thread. Idempotent - a second Start while the thread is running is a no-op. If the key
    /// fails to open we log and return; <see cref="IsActive"/> still works as a one-shot read
    /// but <see cref="Changed"/> will not fire.
    /// </summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_thread != null) return;

            int openStatus = RegOpenKeyExW(HKEY_CURRENT_USER, KeyPath, 0, KEY_NOTIFY | KEY_QUERY_VALUE, out IntPtr hKey);
            if (openStatus != ERROR_SUCCESS)
            {
                WPFLog.Log($"CommunicationsDucking: RegOpenKeyExW failed with {openStatus}");
                return;
            }

            // Manual-reset for the registry event so the signal sticks across our re-arm.
            IntPtr hRegEvent = CreateEventW(IntPtr.Zero, true, false, null);
            if (hRegEvent == IntPtr.Zero)
            {
                RegCloseKey(hKey);
                WPFLog.Log("CommunicationsDucking: CreateEventW (reg) failed");
                return;
            }

            // Auto-reset wake event so a single SetEvent in Stop wakes the thread exactly once.
            IntPtr hWakeEvent = CreateEventW(IntPtr.Zero, false, false, null);
            if (hWakeEvent == IntPtr.Zero)
            {
                Kernel32.CloseHandle(hRegEvent);
                RegCloseKey(hKey);
                WPFLog.Log("CommunicationsDucking: CreateEventW (wake) failed");
                return;
            }

            _hKey = hKey;
            _hRegEvent = hRegEvent;
            _hWakeEvent = hWakeEvent;

            _thread = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = "VolumeTrayApp.CommunicationsDucking",
            };
            _thread.Start();
        }
    }

    /// <summary>
    /// Signals the wake event so the wait thread unwinds, joins it briefly, then closes the key
    /// and both events. Idempotent - a Stop while nothing is running is a no-op.
    /// </summary>
    public static void Stop()
    {
        Thread? toJoin;
        IntPtr hWake;
        lock (Gate)
        {
            if (_thread == null) return;
            toJoin = _thread;
            hWake = _hWakeEvent;
        }

        if (hWake != IntPtr.Zero) SetEvent(hWake);
        try { toJoin?.Join(500); } catch { }

        lock (Gate)
        {
            if (_hRegEvent != IntPtr.Zero) Kernel32.CloseHandle(_hRegEvent);
            if (_hWakeEvent != IntPtr.Zero) Kernel32.CloseHandle(_hWakeEvent);
            if (_hKey != IntPtr.Zero) RegCloseKey(_hKey);
            _hRegEvent = IntPtr.Zero;
            _hWakeEvent = IntPtr.Zero;
            _hKey = IntPtr.Zero;
            _thread = null;
        }
    }

    private static void WatchLoop()
    {
        // Snapshot the handles once outside the gate. WatchLoop is the only writer to these
        // fields during a watch session, and Stop only mutates them after the thread joins.
        IntPtr hKey = _hKey;
        IntPtr hRegEvent = _hRegEvent;
        IntPtr[] handles = [_hWakeEvent, hRegEvent];

        while (true)
        {
            int status = RegNotifyChangeKeyValue(hKey, false, REG_NOTIFY_CHANGE_LAST_SET, hRegEvent, true);
            if (status != ERROR_SUCCESS)
            {
                WPFLog.Log($"CommunicationsDucking: RegNotifyChangeKeyValue failed with {status}");
                return;
            }

            // bWaitAll = false + lowest-index-wins: a tie between wake and reg returns wake (slot 0)
            // so Stop is observed even if a registry write lands on the same kernel pass.
            uint result = WaitForMultipleObjects((uint)handles.Length, handles, false, INFINITE);
            if (result == WAIT_FAILED) return;
            if (result == WAIT_OBJECT_0) return; // wake event - unwind cleanly

            ResetEvent(hRegEvent);

            try { Changed?.Invoke(); }
            catch (Exception ex) { WPFLog.Log($"CommunicationsDucking.Changed: {ex.Message}"); }
        }
    }
}
