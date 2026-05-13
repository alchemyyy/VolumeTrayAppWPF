using System.IO;
using Microsoft.Win32;
using VolumeTrayAppWPF.Interop;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Services;

/// <summary>
/// Owns the app's entries under each user's Start Menu Programs folder.
/// One shortcut per installed scope (Local AppData, Program Files), targeting that scope's exe.
/// Naming, applied per profile based on that profile's view of the install state:
///   - exactly one recognized scope installed for the profile: "VolumeTrayAppWPF.lnk"
///   - two or more (incl. Store-detected): each gets a type suffix, e.g.
///     "VolumeTrayAppWPF (Local).lnk" / "VolumeTrayAppWPF (System).lnk"
/// All managed shortcuts live in each user's per-user
/// <c>%AppData%\Microsoft\Windows\Start Menu\Programs</c> rather than the machine-wide
/// CommonPrograms folder, so cross-scope renames never need elevation: any context that owns
/// a profile owns every shortcut in it. System install/uninstall runs through an elevated
/// path with <see cref="Sync"/>(allUsers: true) so every user profile - and the Default
/// template profile - is reconciled in one pass.
/// The Store entry itself isn't a managed .lnk (Windows renders it from the MSIX manifest);
/// a Store detection only contributes to the suffix count.
/// </summary>
public static class StartMenuShortcut
{
    public const string LocalSuffix = "Local";
    public const string SystemSuffix = "System";

    // Per-profile relative paths. Kept as fixed filesystem strings rather than reading off
    // SpecialFolder.Programs for non-current profiles because SpecialFolder resolution only
    // works for the current user. Folder redirection via GPO on Programs is rare in practice
    // and would lose visibility for redirected users either way - canonical path covers the
    // common case across every profile uniformly.
    private const string ProgramsRelativePath =
        @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs";

    private static string LocalAppDataExeRelativePath =>
        @"AppData\Local\" + Program.ApplicationName + @"\" + InstallationService.InstalledEXEFileName;

    private static string PlainFileName => $"{Program.ApplicationName}.lnk";
    private static string LocalSuffixedFileName => $"{Program.ApplicationName} ({LocalSuffix}).lnk";
    private static string SystemSuffixedFileName => $"{Program.ApplicationName} ({SystemSuffix}).lnk";

    /// <summary>
    /// Reconciles Start Menu shortcuts with the currently-detected install state.
    /// <paramref name="removingScope"/> lets uninstall flows pre-compute the post-state:
    /// the named scope is treated as not-installed even if its file is still on disk
    /// (the bat hasn't run yet). Pass null for install flows / unconditional sync.
    /// <paramref name="allUsers"/> controls profile coverage: <c>false</c> touches only the
    /// current user (suitable for Local install/uninstall and live-UI refresh), <c>true</c>
    /// walks every registered user profile plus the Default template (requires admin to
    /// actually succeed for non-current profiles - caller is responsible for elevation).
    /// Idempotent: safe to call repeatedly. Best-effort - file errors are logged and swallowed
    /// so a transient permission glitch never aborts an install.
    /// </summary>
    public static void Sync(InstallScope? removingScope = null, bool allUsers = false)
    {
        try
        {
            // System install and Store presence are machine-global facts (one Program Files
            // exe, one MSIX package). Compute once and apply across every profile we touch.
            List<InstallationInfo> infos = InstallationService.DetectAll();
            bool systemInstalled = removingScope != InstallScope.ProgramFiles
                && IsConsideredInstalled(infos, InstallScope.ProgramFiles);
            bool storeInstalled = removingScope != InstallScope.WindowsStore
                && IsConsideredInstalled(infos, InstallScope.WindowsStore);
            string systemExe = InstallationService.ProgramFilesInstallEXE;

            if (!allUsers)
            {
                // Current user only. InstallationService already resolves LocalAppData for
                // the caller's user, so we can read Local install state straight from infos.
                bool localInstalled = removingScope != InstallScope.LocalAppData
                    && IsConsideredInstalled(infos, InstallScope.LocalAppData);
                string localExe = InstallationService.LocalAppDataInstallEXE;
                string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                ApplyProfile(
                    programsDir,
                    localInstalled, localExe,
                    systemInstalled, systemExe,
                    storeInstalled);
                return;
            }

            foreach (string profile in EnumerateAllProfilePaths())
            {
                try
                {
                    string profilePrograms = Path.Combine(profile, ProgramsRelativePath);
                    string profileLocalExe = Path.Combine(profile, LocalAppDataExeRelativePath);
                    bool profileLocalInstalled = removingScope != InstallScope.LocalAppData
                        && File.Exists(profileLocalExe);

                    ApplyProfile(
                        profilePrograms,
                        profileLocalInstalled, profileLocalExe,
                        systemInstalled, systemExe,
                        storeInstalled);
                }
                catch (Exception exProfile)
                {
                    WPFLog.Log($"StartMenuShortcut.Sync (profile {profile}): {exProfile.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            WPFLog.Log($"StartMenuShortcut.Sync: {ex.Message}");
        }
    }

    /// <summary>
    /// "Installed" for shortcut purposes covers every state where the install exe is on disk:
    /// fresh install, out-of-date pending update, and the currently-running copy.
    /// </summary>
    private static bool IsConsideredInstalled(List<InstallationInfo> infos, InstallScope scope) =>
        infos.Any(i => i.Scope == scope && i.Status is
            InstallStatus.InstalledUpToDate or
            InstallStatus.InstalledOutOfDate or
            InstallStatus.CurrentlyRunning);

    /// <summary>
    /// Writes the desired-state .lnk set into one profile's Programs folder.
    /// Suffix decisions are local to this profile: a user with only Store + System gets
    /// "(System)" while a user without Local sees plain "VolumeTrayAppWPF".
    /// </summary>
    private static void ApplyProfile(
        string programsDir,
        bool localInstalled, string localExe,
        bool systemInstalled, string systemExe,
        bool storeInstalled)
    {
        int count = (localInstalled ? 1 : 0) + (systemInstalled ? 1 : 0) + (storeInstalled ? 1 : 0);
        bool useSuffixes = count > 1;

        string plainPath = Path.Combine(programsDir, PlainFileName);
        string localSuffixedPath = Path.Combine(programsDir, LocalSuffixedFileName);
        string systemSuffixedPath = Path.Combine(programsDir, SystemSuffixedFileName);

        // Desired state per managed path: an exe target to point at, or null to remove.
        // Plain is owned by whichever single managed scope is installed when count <= 1;
        // suffixed paths are owned by their scope when count >= 2. Store contributes to
        // count but never owns a managed .lnk - its Start Menu entry comes from MSIX.
        string? plainTarget = null;
        string? localSuffixedTarget = null;
        string? systemSuffixedTarget = null;

        if (useSuffixes)
        {
            if (localInstalled) localSuffixedTarget = localExe;
            if (systemInstalled) systemSuffixedTarget = systemExe;
        }
        else if (localInstalled)
            plainTarget = localExe;
        else if (systemInstalled) plainTarget = systemExe;

        ApplyDesired(plainPath, plainTarget);
        ApplyDesired(localSuffixedPath, localSuffixedTarget);
        ApplyDesired(systemSuffixedPath, systemSuffixedTarget);
    }

    private static void ApplyDesired(string lnkPath, string? targetExe)
    {
        if (targetExe == null) TryDelete(lnkPath);
        else TryCreateShortcut(lnkPath, targetExe);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"StartMenuShortcut.TryDelete({path}): {ex.Message}");
        }
    }

    private static void TryCreateShortcut(string lnkPath, string targetExe)
    {
        try
        {
            string? dir = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            ShellLink.Create(lnkPath, targetExe, Program.ApplicationName);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"StartMenuShortcut.TryCreateShortcut({lnkPath}): {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates user profile root paths to walk in allUsers mode.
    /// Sources, in order:
    ///   1. The current user's profile (always - mirrors single-user mode and covers same-user UAC).
    ///   2. HKLM ProfileList, filtered to S-1-5-21-* SIDs (real interactive accounts;
    ///      skips the synthetic LocalSystem / LocalService / NetworkService profiles).
    ///   3. The Default profile (<c>C:\Users\Default</c>), so accounts created after a System
    ///      install inherit the entry on first sign-in.
    /// De-duplicates against the current user so the explicit yield above doesn't double-fire
    /// when ProfileList includes the running user.
    /// </summary>
    private static IEnumerable<string> EnumerateAllProfilePaths()
    {
        string currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return currentProfile;

        using (RegistryKey? root = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
        {
            if (root != null)
            {
                foreach (string sid in root.GetSubKeyNames())
                {
                    if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;

                    using RegistryKey? sub = root.OpenSubKey(sid);
                    if (sub?.GetValue("ProfileImagePath") is not string path) continue;
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;

                    if (string.Equals(
                            PathNormalization.Normalize(path),
                            PathNormalization.Normalize(currentProfile),
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    yield return path;
                }
            }
        }

        string? defaultProfile = GetDefaultProfilePath();
        if (defaultProfile != null) yield return defaultProfile;
    }

    private static string? GetDefaultProfilePath()
    {
        // C:\Users\Default in the standard layout, but resolve via the current user's profile
        // parent so a non-default Users root (e.g. moved-to-D: setups) still works.
        string current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent)) return null;
        string defaultProfile = Path.Combine(parent, "Default");
        return Directory.Exists(defaultProfile) ? defaultProfile : null;
    }
}
