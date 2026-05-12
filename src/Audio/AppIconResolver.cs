using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VolumeTrayAppWPF.Audio.Interop;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Models;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Audio;

// Resolves an audio session's process to a refcounted, cached, frozen WPF icon. Three concerns:
//
//   1. Extraction. Three-layer chain mirrors EarTrumpet:
//        System sounds (PID==0):     audiosrv.dll resource 203 (legacy mixer's speaker glyph)
//        App-supplied icon path:     IAudioSessionControl.GetIconPath() (Discord, Teams, etc.)
//        UWP/AppX:                   GetApplicationUserModelID -> SHCreateItemInKnownFolder
//                                    (AppsFolder, ...) -> IShellItemImageFactory.GetImage
//        Win32 desktop:              PathParseIconLocationW splits "exe.exe,N" -> PE-resource
//                                    extraction; no ordinal -> SHCreateItemFromParsingName
//
//   2. Transparent-border crop. UWP Square44x44Logo.targetsize-* assets ship with internal
//      padding around the glyph for taskbar consistency; desktop ICO frames typically fill
//      edge-to-edge. After extraction we crop the alpha bounding box and re-square so WPF's
//      Stretch=Uniform doesn't reintroduce letterbox padding. Bypassed when padding is small.
//
//   3. Refcounted cache with bounded LRU. Two tiers: (a) identity cache keyed by source identity
//      (path+ordinal or AUMID); (b) content cache keyed by a 64-bit hash of the raw extracted
//      pixels (catches different sources with identical icons). Every Acquire returns an
//      IconHandle whose Dispose decrements the refcount. On refcount=0 the entry parks in the
//      LRU "limbo" queue; a subsequent Acquire for the same icon revives it. Overflow of the
//      queue (bound = AppSettings.IconLruLimit, default 10) evicts the oldest dead entry.
internal static class AppIconResolver
{
    // Target raster size for shell-icon-factory and PE-resource extraction. The flyout displays
    // the icon at 22 device-independent pixels; 48 leaves headroom up to ~200% DPI without
    // upscaling. After extraction we crop the alpha bounding box and re-square the result, then
    // WPF's HighQuality scaling handles the down-sample.
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

    // Crop tuning. ALPHA_THRESHOLD ignores antialiased near-transparent pixels; MIN_OPAQUE_RUN
    // requires a run of contiguous opaque pixels per row before that row contributes to the bbox
    // (sparse AA noise won't anchor it); MIN_PAD_RATIO bypasses cropping when every side is
    // already tight (typical desktop ICO frame).
    private const int ALPHA_THRESHOLD = 16;
    private const int MIN_OPAQUE_RUN = 2;
    private const double MIN_PAD_RATIO = 0.10;

    // Identity-key prefixes. Stable strings - changing them invalidates the cache invariants.
    private const string KEY_SYS = "sys";
    private const string KEY_PE = "pe";
    private const string KEY_SHELL_UWP = "shell|uwp";
    private const string KEY_SHELL_DESKTOP = "shell|desktop";

    private static readonly Guid ShellItem2IID = typeof(IShellItem2).GUID;

    // All cache state lives under one lock. Acquire is off the UI thread but called rarely (once
    // per new session); single-lock contention is dwarfed by the shell-namespace call it guards.
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<string, CacheEntry> s_byIdentity = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, CacheEntry> s_byContent = new();
    private static readonly LinkedList<CacheEntry> s_lru = new();

    /// <summary>
    /// One refcounted reference to a cached icon. Calling <see cref="Dispose"/> decrements
    /// exactly once; double-dispose is a no-op. The wrapped <see cref="Icon"/> is frozen and
    /// safe to bind from any thread.
    /// </summary>
    public sealed class IconHandle : IDisposable
    {
        internal readonly CacheEntry Entry;
        private int _disposed;

        public ImageSource Icon => Entry.Bitmap;

        internal IconHandle(CacheEntry entry) { Entry = entry; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ReleaseEntry(Entry);
        }
    }

    // A single cached icon. One entry can be reachable under multiple identity keys (content
    // dedup folds aliases onto the same entry), so identity keys are a list - eviction must
    // remove every one of them. LruNode is non-null iff the entry sits in s_lru with RefCount=0.
    internal sealed class CacheEntry
    {
        public BitmapSource Bitmap = null!;
        public long ContentHash;
        public List<string> IdentityKeys = new();
        public int RefCount;
        public LinkedListNode<CacheEntry>? LruNode;
    }

    /// <summary>
    /// Resolves an icon for a session and returns a refcounted handle, or null on failure.
    /// Callers MUST dispose the handle when the session no longer needs the icon; the resolver
    /// keeps a small set of recently-dropped entries in an LRU queue so re-acquiring is free.
    /// </summary>
    public static IconHandle? Acquire(IAudioSessionControl control, uint processId, bool isSystemSounds)
    {
        try
        {
            if (isSystemSounds)
            {
                IconHandle? hit = TryAcquireIdentity(KEY_SYS);
                if (hit != null) return hit;

                string sysAudioPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    SYSTEM_SOUNDS_DLL);
                BitmapSource? sysRaw = ExtractFromPEResource(sysAudioPath, SYSTEM_SOUNDS_ICON_ORDINAL);
                return sysRaw == null ? null : MemoizeAndCrop(sysRaw, KEY_SYS);
            }

            // App-supplied icon path takes precedence when present (and non-empty) - apps like
            // Discord, Teams, and Spotify publish here.
            string sessionIconPath = string.Empty;
            try { control.GetIconPath(out sessionIconPath); }
            catch { /* Session may already be torn down; fall back to process-based resolution. */ }

            // UWP first when the process is packaged. Falls through to the desktop branch if the
            // AUMID lookup or shell resolution fails (e.g. sub-process whose AUMID doesn't resolve).
            if (IsPackagedProcess(processId))
            {
                string AUMID = GetApplicationUserModelID(processId);
                if (!string.IsNullOrEmpty(AUMID))
                {
                    string canonical = string.Equals(AUMID, CORTANA_BAD_AUMID, StringComparison.OrdinalIgnoreCase)
                        ? CORTANA_GOOD_AUMID
                        : AUMID;
                    string uwpKey = KEY_SHELL_UWP + "|" + canonical;
                    IconHandle? hit = TryAcquireIdentity(uwpKey);
                    if (hit != null) return hit;

                    BitmapSource? UWPRaw = ExtractFromShell(canonical, isUWP: true);
                    if (UWPRaw != null) return MemoizeAndCrop(UWPRaw, uwpKey);
                }
            }

            // Desktop branch. Prefer the session-supplied path; fall back to the process exe path.
            string desktopPath = !string.IsNullOrWhiteSpace(sessionIconPath)
                ? Environment.ExpandEnvironmentVariables(sessionIconPath.TrimStart('@'))
                : ProcessHelper.GetProcessImagePath(processId) ?? string.Empty;

            if (string.IsNullOrEmpty(desktopPath)) return null;

            return ResolveDesktop(desktopPath);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AppIconResolver.Acquire failed: pid={processId} {ex}");
            return null;
        }
    }

    // Desktop branch: parse "path,N" first (PE resource by ordinal); fall through to the shell
    // when the ordinal is absent or invalid. Cache key is computed before extraction so a hit
    // short-circuits the whole pipeline.
    private static IconHandle? ResolveDesktop(string path)
    {
        StringBuilder iconPath = new(path);
        int iconIndex = IconExtraction.PathParseIconLocationW(iconPath);

        if (iconIndex != 0)
        {
            int ordinal = Math.Abs(iconIndex);
            string normalized = NormalizePath(iconPath.ToString());
            string peKey = KEY_PE + "|" + normalized + "|" + ordinal.ToString();
            IconHandle? hit = TryAcquireIdentity(peKey);
            if (hit != null) return hit;

            BitmapSource? peIcon = ExtractFromPEResource(iconPath.ToString(), ordinal);
            if (peIcon != null) return MemoizeAndCrop(peIcon, peKey);
        }

        // libmpv-based apps (e.g. Plex) sometimes publish "C:\foo\foo.exe,-IDI_ICON1" - an
        // unparseable ordinal that produces 0 above. Strip the marker and fall through to the
        // shell. Ref: https://github.com/mpv-player/mpv/issues/7269
        string shellPath = path;
        if (shellPath.Contains(INVALID_ORDINAL_MARKER))
        {
            shellPath = shellPath.Remove(shellPath.LastIndexOf(INVALID_ORDINAL_MARKER));
        }

        string shellKey = KEY_SHELL_DESKTOP + "|" + NormalizePath(shellPath);
        IconHandle? shellHit = TryAcquireIdentity(shellKey);
        if (shellHit != null) return shellHit;

        BitmapSource? shellIcon = ExtractFromShell(shellPath, isUWP: false);
        return shellIcon == null ? null : MemoizeAndCrop(shellIcon, shellKey);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }

    // Tier-1 lookup. Revives an LRU-parked entry and returns a fresh handle on hit; null on miss.
    private static IconHandle? TryAcquireIdentity(string identityKey)
    {
        lock (s_cacheLock)
        {
            if (!s_byIdentity.TryGetValue(identityKey, out CacheEntry? entry)) return null;
            Revive(entry);
            return new IconHandle(entry);
        }
    }

    // Caller's freshly-extracted bitmap goes through content-dedup, crop, and double-insert.
    // Returns a fresh handle.
    private static IconHandle MemoizeAndCrop(BitmapSource raw, string identityKey)
    {
        long contentHash = HashPixels(raw);

        lock (s_cacheLock)
        {
            // Re-check identity in case another thread filled it while we were extracting.
            if (s_byIdentity.TryGetValue(identityKey, out CacheEntry? existing))
            {
                Revive(existing);
                return new IconHandle(existing);
            }

            // Content dedup - different identity key, same pixel content. Attach this key to the
            // existing entry rather than producing a duplicate.
            if (s_byContent.TryGetValue(contentHash, out CacheEntry? byContent))
            {
                byContent.IdentityKeys.Add(identityKey);
                s_byIdentity[identityKey] = byContent;
                Revive(byContent);
                return new IconHandle(byContent);
            }

            BitmapSource cropped = CropTransparentBorder(raw);
            CacheEntry entry = new()
            {
                Bitmap = cropped,
                ContentHash = contentHash,
                RefCount = 1,
            };
            entry.IdentityKeys.Add(identityKey);
            s_byIdentity[identityKey] = entry;
            s_byContent[contentHash] = entry;
            return new IconHandle(entry);
        }
    }

    // Must run under s_cacheLock. Pulls the entry out of LRU limbo if parked, then bumps refcount.
    private static void Revive(CacheEntry entry)
    {
        if (entry.LruNode != null)
        {
            s_lru.Remove(entry.LruNode);
            entry.LruNode = null;
        }
        entry.RefCount++;
    }

    // Called by IconHandle.Dispose. On refcount hitting zero the entry moves to the LRU "limbo"
    // queue; overflow evicts the oldest dead entry from both dictionaries.
    internal static void ReleaseEntry(CacheEntry entry)
    {
        lock (s_cacheLock)
        {
            entry.RefCount--;
            if (entry.RefCount > 0) return;
            if (entry.RefCount < 0)
            {
                // Defensive - indicates a double-release bug elsewhere. Clamp and leave the entry
                // in place without queueing for eviction.
                entry.RefCount = 0;
                string firstKey = entry.IdentityKeys.Count > 0 ? entry.IdentityKeys[0] : "?";
                WPFLog.Log($"AppIconResolver.ReleaseEntry refcount underflow on {firstKey}");
                return;
            }

            if (entry.LruNode == null)
            {
                entry.LruNode = s_lru.AddFirst(entry);
            }

            int limit = AppServices.Settings?.IconLruLimit ?? AppSettings.IconLruLimitDefault;
            while (s_lru.Count > limit)
            {
                LinkedListNode<CacheEntry>? tail = s_lru.Last;
                if (tail == null) break;
                CacheEntry victim = tail.Value;
                s_lru.RemoveLast();
                victim.LruNode = null;
                s_byContent.Remove(victim.ContentHash);
                for (int i = 0; i < victim.IdentityKeys.Count; i++)
                {
                    s_byIdentity.Remove(victim.IdentityKeys[i]);
                }
                // Frozen BitmapSource becomes unreachable and is reclaimed by GC.
            }
        }
    }

    // Crops the transparent border and re-squares so the visible glyph fills the bitmap
    // edge-to-edge. Returns the original (frozen) on negligible padding or on any failure -
    // this is a polish path, not a correctness one.
    private static BitmapSource CropTransparentBorder(BitmapSource source)
    {
        try
        {
            BitmapSource src = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = src.PixelWidth;
            int height = src.PixelHeight;
            if (width <= 0 || height <= 0) return FreezeIfNeeded(source);

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            src.CopyPixels(pixels, stride, 0);

            int minX = width, minY = height, maxX = -1, maxY = -1;

            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                int run = 0;
                int rowMinX = -1, rowMaxX = -1;
                for (int x = 0; x < width; x++)
                {
                    byte alpha = pixels[rowBase + x * 4 + 3];
                    if (alpha > ALPHA_THRESHOLD)
                    {
                        run++;
                        if (run >= MIN_OPAQUE_RUN)
                        {
                            int firstInRun = x - run + 1;
                            if (rowMinX < 0 || firstInRun < rowMinX) rowMinX = firstInRun;
                            rowMaxX = x;
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }
                if (rowMaxX >= 0)
                {
                    if (rowMinX < minX) minX = rowMinX;
                    if (rowMaxX > maxX) maxX = rowMaxX;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return FreezeIfNeeded(source);

            int leftPad = minX;
            int topPad = minY;
            int rightPad = width - 1 - maxX;
            int bottomPad = height - 1 - maxY;
            int minPad = Math.Min(Math.Min(leftPad, rightPad), Math.Min(topPad, bottomPad));
            int padGate = (int)Math.Ceiling(width * MIN_PAD_RATIO);
            if (minPad < padGate) return FreezeIfNeeded(source);

            int cropW = maxX - minX + 1;
            int cropH = maxY - minY + 1;
            int side = Math.Max(cropW, cropH);
            int cx = minX + cropW / 2;
            int cy = minY + cropH / 2;
            int half = side / 2;
            int sx = Math.Max(0, Math.Min(width - side, cx - half));
            int sy = Math.Max(0, Math.Min(height - side, cy - half));
            int sw = Math.Min(side, width - sx);
            int sh = Math.Min(side, height - sy);

            CroppedBitmap cropped = new(src, new Int32Rect(sx, sy, sw, sh));
            cropped.Freeze();
            return cropped;
        }
        catch (Exception ex)
        {
            WPFLog.Log($"AppIconResolver.CropTransparentBorder failed: {ex}");
            return FreezeIfNeeded(source);
        }
    }

    private static BitmapSource FreezeIfNeeded(BitmapSource source)
    {
        if (source.CanFreeze && !source.IsFrozen) source.Freeze();
        return source;
    }

    // FNV-1a-64 over the raw pixel buffer plus dimensions. Non-crypto, process-local, sub-ms on
    // 9 KB. Dimensions folded in so two same-byte buffers at different shapes can't collide.
    private static long HashPixels(BitmapSource source)
    {
        try
        {
            BitmapSource src = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = src.PixelWidth * 4;
            byte[] pixels = new byte[stride * src.PixelHeight];
            src.CopyPixels(pixels, stride, 0);

            const ulong OFFSET = 14695981039346656037UL;
            const ulong PRIME = 1099511628211UL;
            ulong hash = OFFSET;
            for (int i = 0; i < pixels.Length; i++)
            {
                hash ^= pixels[i];
                hash *= PRIME;
            }
            hash ^= (ulong)src.PixelWidth;
            hash *= PRIME;
            hash ^= (ulong)src.PixelHeight;
            hash *= PRIME;
            return (long)hash;
        }
        catch
        {
            // Last-resort fallback - object identity so two distinct instances never collide.
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(source);
        }
    }

    // -- Extraction primitives. Called only on cache miss; behavior unchanged from prior revision.

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
            WPFLog.Log($"AppIconResolver.ExtractFromPEResource {path},{iconOrdinal} {ex}");
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) User32.DestroyIcon(hIcon);
            IconExtraction.FreeLibrary(hModule);
        }
    }

    // Shell-icon-factory extraction: SHCreateItem* -> IShellItemImageFactory.GetImage -> HBITMAP.
    // For UWP the path is an AUMID resolved against AppsFolder; for desktop it's a file system path.
    private static BitmapSource? ExtractFromShell(string path, bool isUWP)
    {
        string canonical = isUWP && string.Equals(path, CORTANA_BAD_AUMID, StringComparison.OrdinalIgnoreCase)
            ? CORTANA_GOOD_AUMID
            : path;

        IShellItem2? shellItem = null;
        try
        {
            try
            {
                shellItem = IconExtraction.SHCreateItemInKnownFolder(
                    IconExtraction.AppsFolderID,
                    IconExtraction.KF_FLAG_DONT_VERIFY,
                    canonical,
                    ShellItem2IID);
            }
            catch
            {
                // Apps-folder lookup fails for plain file paths; fall through to parsing-name.
                shellItem = IconExtraction.SHCreateItemFromParsingName(canonical, IntPtr.Zero, ShellItem2IID);
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
            WPFLog.Log($"AppIconResolver.ExtractFromShell {canonical} {ex}");
            return null;
        }
        finally
        {
            Safe.Release(shellItem);
        }
    }

    // UWP detection: GetPackageId returns ERROR_INSUFFICIENT_BUFFER for packaged processes (because
    // we pass a zero-byte buffer). Anything else (S_OK on a non-packaged process never happens, and
    // various failures for unreachable processes) means "not packaged".
    private static bool IsPackagedProcess(uint processId)
    {
        IntPtr handle = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return false;

        try
        {
            int bufferSize = 0;
            int hr = IconExtraction.GetPackageId(handle, ref bufferSize, IntPtr.Zero);
            return hr == NativeErrors.ERROR_INSUFFICIENT_BUFFER;
        }
        finally { Kernel32.CloseHandle(handle); }
    }

    private static string GetApplicationUserModelID(uint processId)
    {
        IntPtr handle = Kernel32.OpenProcess(
            Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero) return string.Empty;

        try
        {
            int length = IconExtraction.MAX_AUMID_LEN;
            StringBuilder buffer = new(length);
            int hr = IconExtraction.GetApplicationUserModelId(handle, ref length, buffer);
            return hr == NativeErrors.S_OK ? buffer.ToString() : string.Empty;
        }
        finally { Kernel32.CloseHandle(handle); }
    }
}
