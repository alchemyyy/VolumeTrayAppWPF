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

    // CreateFile / DeviceIoControl flags consumed by the KS audio pin-property query path
    // (AudioDevice.QueryKsAudioDataRanges). FILE_SHARE_READ|WRITE lets us open the audio
    // device's KS filter while another client (Windows, our own engine) has it open.
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
