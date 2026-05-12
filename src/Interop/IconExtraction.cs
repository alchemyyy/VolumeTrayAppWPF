using System.Runtime.InteropServices;
using System.Text;

namespace VolumeTrayAppWPF.Interop;

// Win32 + COM bindings used by AppIconResolver. Mirrors the trimmed-down subset of EarTrumpet's
// interop layout that the resolver actually consumes:
//   - PE-resource icon extraction:    LoadLibraryEx + FindResource + LookupIconIdFromDirectoryEx
//                                     + CreateIconFromResourceEx
//   - Shell-namespace icons (UWP):    SHCreateItemInKnownFolder(AppsFolder, AUMID, ...)
//                                     + IShellItemImageFactory.GetImage
//   - Shell-namespace icons (file):   SHCreateItemFromParsingName + IShellItemImageFactory.GetImage
//   - UWP detection + AUMID lookup:   GetPackageId / GetApplicationUserModelId
//   - Indirect icon path parser:      PathParseIconLocationW
//
// Lives in src/Interop/ (not Audio/Interop/) because the surface has zero audio concerns - it's
// shell + PE-resource interop that happens to be consumed by AppIconResolver. Process P/Invokes
// (OpenProcess / CloseHandle / PROCESS_QUERY_LIMITED_INFORMATION) live in Kernel32.cs;
// DestroyIcon lives in User32.cs; S_OK / ERROR_INSUFFICIENT_BUFFER live in NativeErrors.cs.
internal static class IconExtraction
{
    public const int KF_FLAG_DONT_VERIFY = 0x00004000;
    public const int LOAD_LIBRARY_AS_DATAFILE = 0x02;
    public const int LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    public const int MAX_AUMID_LEN = 512;

    // Standard PE resource type ordinals (winuser.h: MAKEINTRESOURCE).
    public static readonly IntPtr RT_ICON = new(3);
    public static readonly IntPtr RT_GROUP_ICON = new(14);

    // Known-folder GUID for the Apps folder. Used as the parent folder when resolving a UWP AUMID
    // through SHCreateItemInKnownFolder.
    public static readonly Guid AppsFolderID = new("1E87508D-89C2-42F0-8A7E-645A0F50CA58");

    public enum LoadImageFlags : uint
    {
        LR_DEFAULTCOLOR = 0x00000000,
    }

    public enum IconCursorVersion : int
    {
        Default = 0x00030000,
    }

    // SHGetImageFromShellItem flag set; only RESIZETOFIT is needed for the icon use case.
    public enum SIIGBF : int
    {
        SIIGBF_RESIZETOFIT = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE
    {
        public int cx;
        public int cy;
    }

    // -- kernel32 (icon-resource family only; process P/Invokes live in Kernel32.cs)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryExW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpLibFileName,
        IntPtr hFile,
        int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindResourceW")]
    public static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll")]
    public static extern int SizeofResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", PreserveSig = true)]
    public static extern int GetPackageId(IntPtr hProcess, ref int bufferLength, IntPtr packageId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int GetApplicationUserModelId(
        IntPtr hProcess,
        ref int applicationUserModelIdLength,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder applicationUserModelId);

    // -- user32 (icon-resource family only; DestroyIcon lives in User32.cs)

    [DllImport("user32.dll")]
    public static extern int LookupIconIdFromDirectoryEx(
        IntPtr presbits,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        int cxDesired,
        int cyDesired,
        LoadImageFlags Flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(
        IntPtr presbits,
        int dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        IconCursorVersion dwVer,
        int cxDesired,
        int cyDesired,
        LoadImageFlags Flags);

    // -- shlwapi

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int PathParseIconLocationW(
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconFile);

    // -- gdi32

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    // -- shell32

    // PreserveSig=false here so the marshaller throws on failure HRESULTs; both call sites are wrapped
    // in try/catch and fall through to the next strategy.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern IShellItem2 SHCreateItemInKnownFolder(
        [MarshalAs(UnmanagedType.LPStruct)] Guid kfid,
        uint dwKFFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string pszItem,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern IShellItem2 SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
}

// IShellItem2 is declared empty: the resolver only uses it as the holder type for the COM object
// returned by SHCreateItem*, then immediately QI's to IShellItemImageFactory. Methods would only
// matter if we called them through this interface, which we never do.
[ComImport]
[Guid("7E9FB0D3-919F-4307-AB2E-9B1860310C93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem2
{
}

[ComImport]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    void GetImage(IconExtraction.SIZE size, IconExtraction.SIIGBF flags, out IntPtr hBitmap);
}
