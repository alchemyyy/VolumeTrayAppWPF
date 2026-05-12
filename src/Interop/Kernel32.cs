using System.Runtime.InteropServices;

namespace VolumeTrayAppWPF.Interop;

// Shared kernel32 P/Invokes consumed by both the icon-extraction pipeline and the per-session
// process-lifetime watchers (ProcessHelper / ProcessExitMonitor). One declaration site avoids the
// three-copy drift the pre-refactor layout shipped with.
internal static class Kernel32
{
    // PROCESS_QUERY_LIMITED_INFORMATION is the cheapest right that still resolves a PID to its
    // image path and lets us query AUMID / GetPackageId. Works against UWP and other restricted
    // processes that PROCESS_QUERY_INFORMATION would be refused on.
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}
