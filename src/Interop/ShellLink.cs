using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Interop;

/// <summary>
/// IShellLink / IPersistFile COM wrappers for creating and reading Windows .lnk shortcuts.
/// Centralized here so the autostart shortcut (StartupManager) and the Start Menu Programs
/// shortcuts (StartMenuShortcut) share one COM-glue copy.
/// </summary>
internal static class ShellLink
{
    /// <summary>
    /// Writes a .lnk file at <paramref name="lnkPath"/> pointing at <paramref name="targetExe"/>.
    /// Working directory is set to the target exe's folder; description is shown by Windows in
    /// the shortcut's tooltip and properties dialog.
    /// </summary>
    public static void Create(string lnkPath, string targetExe, string description)
    {
        object? linkObj = null;
        try
        {
            linkObj = new CShellLink();
            IShellLinkW link = (IShellLinkW)linkObj;
            link.SetPath(targetExe);
            string? workDir = Path.GetDirectoryName(targetExe);
            if (!string.IsNullOrEmpty(workDir)) link.SetWorkingDirectory(workDir);
            link.SetDescription(description);

            // Cast through the underlying COM object: CShellLink implements both IShellLinkW
            // and IPersistFile, but the managed interfaces don't share an inheritance chain
            // so casting one to the other trips a static analyzer warning.
            IPersistFile persist = (IPersistFile)linkObj;
            persist.Save(lnkPath, true);
        }
        finally
        {
            Safe.Release(linkObj);
        }
    }

    /// <summary>
    /// Returns the raw target path of an existing .lnk, or null if the file is missing or the
    /// COM read failed. SLGP_RAWPATH keeps environment-variable expansion off so the returned
    /// string is byte-for-byte comparable against known install paths.
    /// </summary>
    public static string? TryRead(string lnkPath)
    {
        object? linkObj = null;
        try
        {
            linkObj = new CShellLink();
            IShellLinkW link = (IShellLinkW)linkObj;
            IPersistFile persist = (IPersistFile)linkObj;
            persist.Load(lnkPath, 0);

            const uint SLGP_RAWPATH = 0x0004;
            StringBuilder sb = new(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            string raw = sb.ToString();
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
        catch
        {
            return null;
        }
        finally
        {
            Safe.Release(linkObj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
