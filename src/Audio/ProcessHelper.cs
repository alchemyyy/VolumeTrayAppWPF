using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VolumeTrayAppWPF.Interop;

namespace VolumeTrayAppWPF.Audio;

// Process metadata lookup for audio sessions. Icon extraction lives in AppIconResolver;
// this class only resolves a PID to a display name + image path.
// QueryFullProcessImageName works against UWP and other restricted processes that
// Process.MainModule.FileName cannot reach, so we go straight to the kernel32 API.
// OpenProcess / CloseHandle / PROCESS_QUERY_LIMITED_INFORMATION live in Interop/Kernel32.cs.
internal static class ProcessHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>
    /// Resolves a PID to the full image path. Returns null on failure (process gone, access denied).
    /// </summary>
    public static string? GetProcessImagePath(uint processId)
    {
        if (processId == 0) return null;

        IntPtr handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return null;

        try
        {
            StringBuilder buffer = new(1024);
            uint size = (uint)buffer.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                return buffer.ToString(0, (int)size);
        }
        finally { Kernel32.CloseHandle(handle); }

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
