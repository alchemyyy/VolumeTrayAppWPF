using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VolumeTrayAppWPF.Audio.Interop;

namespace VolumeTrayAppWPF.Audio;

// Resolves an audio session's process to a frozen WPF icon, mirroring EarTrumpet's three-layer chain:
//   1. System sounds (PID==0):        hardcoded audiosrv.dll resource 203, the same one the legacy mixer uses
//   2. App-supplied icon path:        IAudioSessionControl.GetIconPath() - apps like Discord publish here
//   3. UWP/AppX:                      GetApplicationUserModelId -> SHCreateItemInKnownFolder(AppsFolder, ...)
//                                     -> IShellItemImageFactory.GetImage
//   4. Win32 desktop:                 PathParseIconLocationW splits "exe.exe,N" -> PE-resource extraction;
//                                     when no ordinal is present, falls back to SHCreateItemFromParsingName
// Returns null on any failure - the flyout XAML renders a Segoe Fluent fallback glyph in that case.
internal static class AppIconResolver
{
    // Target raster size for shell-icon-factory and PE-resource extraction. The flyout displays
    // the icon at 22 device-independent pixels; 48 leaves headroom up to ~200% DPI without upscaling,
    // and WPF's HighQuality scaling handles the down-sample.
    private const int ICON_SIZE = 48;

    // System-sounds resource: AudioSrv.dll's icon ordinal 203 is the speaker glyph the legacy
    // Volume Mixer uses for the system-sounds row. Resolved against system32 at runtime.
    private const string SYSTEM_SOUNDS_DLL = "audiosrv.dll";
    private const int SYSTEM_SOUNDS_ICON_ORDINAL = 203;

    // Microsoft ships empty assets for the CortanaUI AUMID; redirect to the package metadata AUMID
    // which has a usable Square44x44Logo. Mirrors EarTrumpet's CanonicalizePath workaround.
    // Ref: https://github.com/File-New-Project/EarTrumpet/issues/1259
    private const string CORTANA_BAD_AUMID = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!CortanaUI";
    private const string CORTANA_GOOD_AUMID = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy!PackageMetadata";

    private const string INVALID_ORDINAL_MARKER = ",-";

    private static readonly Guid ShellItem2Iid = typeof(IShellItem2).GUID;

    /// <summary>
    /// Resolves an icon for a session. Returns null on any failure; the caller should render a fallback.
    /// </summary>
    public static ImageSource? Resolve(IAudioSessionControl control, uint processId, bool isSystemSounds)
    {
        try
        {
            if (isSystemSounds)
            {
                string sysAudioPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    SYSTEM_SOUNDS_DLL);
                return ExtractFromPEResource(sysAudioPath, SYSTEM_SOUNDS_ICON_ORDINAL);
            }

            // App-supplied icon path takes precedence when present (and non-empty). This is what apps
            // like Discord, Teams, and Spotify publish to identify themselves in the volume mixer.
            string sessionIconPath = string.Empty;
            try { control.GetIconPath(out sessionIconPath); }
            catch { /* Session may already be torn down; fall back to process-based resolution. */ }

            // UWP first when the process is packaged. Falls through to the desktop branch if the
            // AUMID lookup or shell resolution fails (e.g. sub-process whose AUMID doesn't resolve).
            if (IsPackagedProcess(processId))
            {
                string aumid = GetApplicationUserModelId(processId);
                if (!string.IsNullOrEmpty(aumid))
                {
                    BitmapSource? uwpIcon = ExtractFromShell(aumid, isUwp: true);
                    if (uwpIcon != null) return uwpIcon;
                }
            }

            // Desktop branch. Prefer the session-supplied path; fall back to the process exe path.
            string desktopPath = !string.IsNullOrWhiteSpace(sessionIconPath)
                ? Environment.ExpandEnvironmentVariables(sessionIconPath.TrimStart('@'))
                : ProcessHelper.GetProcessImagePath(processId) ?? string.Empty;

            if (string.IsNullOrEmpty(desktopPath)) return null;

            return ExtractFromDesktop(desktopPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AppIconResolver.Resolve failed: pid={processId} {ex}");
            return null;
        }
    }

    // Desktop branch: parse "path,N" first (PE resource by ordinal); fall through to the shell
    // when the ordinal is absent or invalid.
    private static BitmapSource? ExtractFromDesktop(string path)
    {
        StringBuilder iconPath = new(path);
        int iconIndex = IconExtraction.PathParseIconLocationW(iconPath);

        if (iconIndex != 0)
        {
            BitmapSource? peIcon = ExtractFromPEResource(iconPath.ToString(), Math.Abs(iconIndex));
            if (peIcon != null) return peIcon;
        }

        // libmpv-based apps (e.g. Plex) sometimes publish "C:\foo\foo.exe,-IDI_ICON1" - an unparseable
        // ordinal that produces 0 above. Strip the marker and fall through to the shell.
        // Ref: https://github.com/mpv-player/mpv/issues/7269
        string shellPath = path;
        if (shellPath.Contains(INVALID_ORDINAL_MARKER))
        {
            shellPath = shellPath.Remove(shellPath.LastIndexOf(INVALID_ORDINAL_MARKER));
        }

        return ExtractFromShell(shellPath, isUwp: false);
    }

    // PE-resource extraction: LoadLibraryEx as a data file, find RT_GROUP_ICON for the requested
    // ordinal, pick the best size match via LookupIconIdFromDirectoryEx, then materialize the icon
    // through CreateIconFromResourceEx. The returned bitmap is frozen so it can cross threads safely.
    private static BitmapSource? ExtractFromPEResource(string path, int iconOrdinal)
    {
        IntPtr hModule = IconExtraction.LoadLibraryExW(
            path,
            IntPtr.Zero,
            IconExtraction.LOAD_LIBRARY_AS_DATAFILE | IconExtraction.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (hModule == IntPtr.Zero) return null;

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            IntPtr groupResInfo = IconExtraction.FindResource(hModule, new IntPtr(iconOrdinal), IconExtraction.RT_GROUP_ICON);
            if (groupResInfo == IntPtr.Zero) return null;

            IntPtr groupResHandle = IconExtraction.LoadResource(hModule, groupResInfo);
            if (groupResHandle == IntPtr.Zero) return null;

            IntPtr groupResData = IconExtraction.LockResource(groupResHandle);
            if (groupResData == IntPtr.Zero) return null;

            int iconId = IconExtraction.LookupIconIdFromDirectoryEx(
                groupResData, true, ICON_SIZE, ICON_SIZE, IconExtraction.LoadImageFlags.LR_DEFAULTCOLOR);
            if (iconId == 0) return null;

            IntPtr iconResInfo = IconExtraction.FindResource(hModule, new IntPtr(iconId), IconExtraction.RT_ICON);
            if (iconResInfo == IntPtr.Zero) return null;

            IntPtr iconResHandle = IconExtraction.LoadResource(hModule, iconResInfo);
            if (iconResHandle == IntPtr.Zero) return null;

            IntPtr iconResData = IconExtraction.LockResource(iconResHandle);
            int iconResSize = IconExtraction.SizeofResource(hModule, iconResInfo);
            if (iconResData == IntPtr.Zero || iconResSize == 0) return null;

            hIcon = IconExtraction.CreateIconFromResourceEx(
                iconResData, iconResSize, true,
                IconExtraction.IconCursorVersion.Default,
                ICON_SIZE, ICON_SIZE,
                IconExtraction.LoadImageFlags.LR_DEFAULTCOLOR);
            if (hIcon == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AppIconResolver.ExtractFromPEResource {path},{iconOrdinal} {ex}");
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) IconExtraction.DestroyIcon(hIcon);
            IconExtraction.FreeLibrary(hModule);
        }
    }

    // Shell-icon-factory extraction: SHCreateItem* -> IShellItemImageFactory.GetImage -> HBITMAP.
    // For UWP the path is an AUMID resolved against AppsFolder; for desktop it's a file system path.
    private static BitmapSource? ExtractFromShell(string path, bool isUwp)
    {
        string canonical = isUwp && string.Equals(path, CORTANA_BAD_AUMID, StringComparison.OrdinalIgnoreCase)
            ? CORTANA_GOOD_AUMID
            : path;

        IShellItem2? shellItem = null;
        try
        {
            try
            {
                shellItem = IconExtraction.SHCreateItemInKnownFolder(
                    IconExtraction.AppsFolderId,
                    IconExtraction.KF_FLAG_DONT_VERIFY,
                    canonical,
                    ShellItem2Iid);
            }
            catch
            {
                // Apps-folder lookup fails for plain file paths; fall through to parsing-name.
                shellItem = IconExtraction.SHCreateItemFromParsingName(canonical, IntPtr.Zero, ShellItem2Iid);
            }

            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                IconExtraction.SIZE size = new() { cx = ICON_SIZE, cy = ICON_SIZE };
                ((IShellItemImageFactory)shellItem).GetImage(size, IconExtraction.SIIGBF.SIIGBF_RESIZETOFIT, out hBitmap);
                if (hBitmap == IntPtr.Zero) return null;

                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) IconExtraction.DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"AppIconResolver.ExtractFromShell {canonical} {ex}");
            return null;
        }
        finally
        {
            if (shellItem != null) Marshal.FinalReleaseComObject(shellItem);
        }
    }

    // UWP detection: GetPackageId returns ERROR_INSUFFICIENT_BUFFER for packaged processes (because
    // we pass a zero-byte buffer). Anything else (S_OK on a non-packaged process never happens, and
    // various failures for unreachable processes) means "not packaged".
    private static bool IsPackagedProcess(uint processId)
    {
        IntPtr handle = IconExtraction.OpenProcess(
            IconExtraction.ProcessFlags.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return false;

        try
        {
            int bufferSize = 0;
            int hr = IconExtraction.GetPackageId(handle, ref bufferSize, IntPtr.Zero);
            return hr == IconExtraction.ERROR_INSUFFICIENT_BUFFER;
        }
        finally { IconExtraction.CloseHandle(handle); }
    }

    private static string GetApplicationUserModelId(uint processId)
    {
        IntPtr handle = IconExtraction.OpenProcess(
            IconExtraction.ProcessFlags.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return string.Empty;

        try
        {
            int length = IconExtraction.MAX_AUMID_LEN;
            StringBuilder buffer = new(length);
            int hr = IconExtraction.GetApplicationUserModelId(handle, ref length, buffer);
            return hr == IconExtraction.S_OK ? buffer.ToString() : string.Empty;
        }
        finally { IconExtraction.CloseHandle(handle); }
    }
}
