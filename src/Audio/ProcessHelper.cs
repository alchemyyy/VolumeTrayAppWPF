using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VolumeTrayAppWPF.Audio;

// Process metadata lookup for audio sessions. Icon extraction lives in AppIconResolver;
// this class only resolves a PID to a display name + image path.
// QueryFullProcessImageName works against UWP and other restricted processes that
// Process.MainModule.FileName cannot reach, so we go straight to the kernel32 API.
internal static class ProcessHelper
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>
    /// Resolves a PID to the full image path. Returns null on failure (process gone, access denied).
    /// </summary>
    public static string? GetProcessImagePath(uint processId)
    {
        if (processId == 0) return null;

        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            StringBuilder buffer = new(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                return buffer.ToString(0, (int)size);
        }
        finally { CloseHandle(handle); }

        return null;
    }

    /// <summary>
    /// Best-effort display name for a session. Order:
    ///  1. Process FileVersionInfo.FileDescription (e.g. "Discord")
    ///  2. Process exe filename without extension (e.g. "Discord")
    ///  3. "Unknown"
    /// </summary>
    public static string GetDisplayNameForProcess(uint processId)
    {
        string? path = GetProcessImagePath(processId);
        if (string.IsNullOrEmpty(path)) return "Unknown";

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription!;
        }
        catch
        {
            // FileVersionInfo can throw FileNotFound (UWP placeholder paths) or access denied.
            // Fall through to filename-only.
        }

        return Path.GetFileNameWithoutExtension(path);
    }
}
