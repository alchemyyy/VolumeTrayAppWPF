using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using VolumeTrayAppWPF.Utils;

namespace VolumeTrayAppWPF.Services;

public enum InstallScope
{
    LocalAppData,
    ProgramFiles,
    WindowsStore,
}

public enum InstallStatus
{
    NotInstalled,
    InstalledUpToDate,
    InstalledOutOfDate,
    CurrentlyRunning,
}

/// <summary>
/// One row's worth of state for the Settings &gt; About / Misc &gt; Installation section.
/// <see cref="InstalledVersion"/> is the build number registered in the Uninstall registry entry,
/// not a value read from the installed exe (we don't want to load another assembly to read its embedded
/// BuildInfo constant).
/// </summary>
public sealed record InstallationInfo(
    InstallScope Scope,
    string InstallEXEPath,
    InstallStatus Status,
    int? InstalledVersion);

public sealed record InstallResult(bool Success, string? ErrorMessage = null, bool UserCancelled = false);

/// <summary>
/// Manages copying the running exe into Program Files / LocalAppData and registering an Add-or-Remove-Programs entry.
/// Uninstall is driven by <see cref="UninstallScript"/>:
/// the WPF <see cref="WPF.UninstallerWindow"/> collects keep-vs-delete-settings,
/// then <see cref="RunUninstall"/> writes a self-deleting .bat to <c>%TEMP%</c>
/// and queues <c>Application.Shutdown()</c> so the bat can take over file/registry cleanup
/// once the watcher and monitored process have exited cleanly.
/// </summary>
public static class InstallationService
{
    public static string InstalledEXEFileName => Program.ApplicationName + ".exe";

    public static string LocalAppDataInstallDir =>
        Program.LocalAppDataRoot;

    public static string LocalAppDataInstallEXE =>
        Path.Combine(LocalAppDataInstallDir, InstalledEXEFileName);

    public static string ProgramFilesInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.SharedRootFolderName);

    public static string ProgramFilesInstallEXE =>
        Path.Combine(ProgramFilesInstallDir, InstalledEXEFileName);

    public static string WindowsAppsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    /// <summary>
    /// True when the current process holds Administrator group membership in its access token.
    /// Mirrors the previous ElevationService.IsElevated().
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"InstallationService.IsElevated: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// True when the current process is running from a path under %ProgramFiles%\WindowsApps\ (i.e. an MSIX/Store
    /// install). Used purely for detection - the project doesn't ship MSIX.
    /// </summary>
    public static bool IsRunningFromWindowsStore()
    {
        string? current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current)) return false;
        try
        {
            string root = WindowsAppsRoot;
            return current.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static List<InstallationInfo> DetectAll()
    {
        const int currentVersion = BuildInfo.BuildNumber;
        string currentPath = PathNormalization.Normalize(Environment.ProcessPath);

        List<InstallationInfo> results =
        [
            DetectFile(InstallScope.LocalAppData, LocalAppDataInstallEXE,
                WindowsUninstallRegistry.Scope.CurrentUser, currentPath, currentVersion),

            DetectFile(InstallScope.ProgramFiles, ProgramFilesInstallEXE,
                WindowsUninstallRegistry.Scope.LocalMachine, currentPath, currentVersion),

            DetectStore(currentPath)

        ];
        return results;
    }

    private static InstallationInfo DetectFile(InstallScope scope, string installEXE,
        WindowsUninstallRegistry.Scope regScope, string currentPath, int currentVersion)
    {
        bool fileExists = File.Exists(installEXE);

        // Clean orphan registry entries: if the file is gone, drop any leftover Uninstall key so the
        // entry doesn't haunt Add/Remove Programs.
        WindowsUninstallRegistry.Entry? entry = WindowsUninstallRegistry.Read(regScope);
        if (!fileExists && entry != null)
        {
            WindowsUninstallRegistry.Remove(regScope);
            entry = null;
        }

        if (!fileExists) return new InstallationInfo(scope, installEXE, InstallStatus.NotInstalled, null);

        bool running = string.Equals(currentPath, PathNormalization.Normalize(installEXE), StringComparison.OrdinalIgnoreCase);
        if (running)
            return new InstallationInfo(scope, installEXE, InstallStatus.CurrentlyRunning, entry?.DisplayVersion);

        int? installed = entry?.DisplayVersion;
        if (installed.HasValue && installed.Value < currentVersion)
            return new InstallationInfo(scope, installEXE, InstallStatus.InstalledOutOfDate, installed);
        return new InstallationInfo(scope, installEXE, InstallStatus.InstalledUpToDate, installed);
    }

    private static InstallationInfo DetectStore(string currentPath)
    {
        // No MSIX in this project, so the only Store state we recognize is "running from WindowsApps".
        // Anything else is reported as NotInstalled with no install button shown.
        if (IsRunningFromWindowsStore())
            return new InstallationInfo(InstallScope.WindowsStore, currentPath, InstallStatus.CurrentlyRunning, null);
        return new InstallationInfo(InstallScope.WindowsStore, string.Empty, InstallStatus.NotInstalled, null);
    }

    /// <summary>
    /// Copy the running exe into <see cref="LocalAppDataInstallDir"/> and write the HKCU uninstall entry.
    /// No UAC needed.
    /// </summary>
    public static InstallResult InstallToLocalAppData()
    {
        string source = Environment.ProcessPath ?? string.Empty;
        if (!File.Exists(source)) return new InstallResult(false, "Cannot determine running executable path");

        try
        {
            Directory.CreateDirectory(LocalAppDataInstallDir);
            string dest = LocalAppDataInstallEXE;
            if (string.Equals(PathNormalization.Normalize(source), PathNormalization.Normalize(dest), StringComparison.OrdinalIgnoreCase))
            {
                // Same file - nothing to copy, just refresh the registry entry below
            }
            else
                File.Copy(source, dest, overwrite: true);

            WindowsUninstallRegistry.Write(WindowsUninstallRegistry.Scope.CurrentUser,
                LocalAppDataInstallDir, BuildInfo.BuildNumber);
            StartMenuShortcut.Sync();
            return new InstallResult(true);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"InstallationService.InstallToLocalAppData: {ex}");
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>
    /// System-wide install. If the current process isn't elevated, relaunches itself with
    /// <c>--admin-action install-system &lt;sourceExe&gt; &lt;buildNumber&gt;</c> through <c>runas</c>, and waits for
    /// completion.
    /// </summary>
    public static InstallResult InstallSystemWide()
    {
        if (IsElevated()) return RunAdminInstallSystem(Environment.ProcessPath ?? string.Empty, BuildInfo.BuildNumber);

        string source = Environment.ProcessPath ?? string.Empty;
        if (!File.Exists(source)) return new InstallResult(false, "Cannot determine running executable path");

        return TryInvokeElevated(
            $"--admin-action install-system \"{source}\" {BuildInfo.BuildNumber}",
            sourceExe: source);
    }

    /// <summary>
    /// Privileged branch of <see cref="InstallSystemWide"/>.
    /// Called from Program.Main when the app is launched with <c>--admin-action install-system</c>.
    /// </summary>
    public static InstallResult RunAdminInstallSystem(string sourceExe, int buildNumber)
    {
        try
        {
            if (!File.Exists(sourceExe)) return new InstallResult(false, $"Source exe not found: {sourceExe}");
            Directory.CreateDirectory(ProgramFilesInstallDir);
            string dest = ProgramFilesInstallEXE;
            if (!string.Equals(PathNormalization.Normalize(sourceExe), PathNormalization.Normalize(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceExe, dest, overwrite: true);
            WindowsUninstallRegistry.Write(WindowsUninstallRegistry.Scope.LocalMachine,
                ProgramFilesInstallDir, buildNumber);
            // System install is elevated by definition - sweep every user profile (plus the
            // Default template) so the entry appears on first sign-in for every account,
            // and any existing per-user Local entry has its suffix updated atomically.
            StartMenuShortcut.Sync(allUsers: true);
            return new InstallResult(true);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"InstallationService.RunAdminInstallSystem: {ex}");
            return new InstallResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Hands the install dir + scope off to <see cref="UninstallScript"/>, which writes a self-deleting .bat to
    /// <c>%TEMP%</c> and spawns it (with <c>runas</c> for HKLM).
    /// Only triggers <c>Application.Shutdown()</c> when the running WPF is itself the install copy being uninstalled -
    /// a portable build that happens to be uninstalling a separate installed copy must keep running.
    /// Returns the spawned bat <see cref="Process"/> (or null on failure / shutting down) so the caller can hook
    /// <c>Exited</c> for live UI refresh.
    /// </summary>
    public static Process? RunUninstall(InstallScope scope, bool deleteSettings)
    {
        string installDir;
        WindowsUninstallRegistry.Scope regScope;
        if (scope == InstallScope.LocalAppData)
        {
            installDir = LocalAppDataInstallDir;
            regScope = WindowsUninstallRegistry.Scope.CurrentUser;
        }
        else if (scope == InstallScope.ProgramFiles)
        {
            installDir = ProgramFilesInstallDir;
            regScope = WindowsUninstallRegistry.Scope.LocalMachine;
        }
        else
            return null;

        // Pre-bat Start Menu sync (current user only): treat the scope being uninstalled as
        // already gone so the current user's Programs folder updates the instant they click,
        // without waiting for the bat. For System uninstall, the bat additionally re-invokes
        // us with --admin-action sync-startmenu (still elevated, no second UAC) to walk every
        // other user profile - that pass covers everyone, this pass just races ahead for the
        // currently-logged-in user's Start Menu so the visible state matches the click.
        StartMenuShortcut.Sync(removingScope: scope);

        Process? batProcess = UninstallScript.Run(installDir, regScope, deleteSettings);

        // Only shut down if THIS process is the install copy.
        // The bat kills any other processes at the install path itself (path-scoped, not name-scoped), so a portable
        // build uninstalling a different installed copy keeps running untouched.
        string runningExe = PathNormalization.Normalize(Environment.ProcessPath);
        string installEXE = PathNormalization.Normalize(Path.Combine(installDir, InstalledEXEFileName));
        bool runningFromInstall = !string.IsNullOrEmpty(runningExe) &&
            string.Equals(runningExe, installEXE, StringComparison.OrdinalIgnoreCase);

        if (runningFromInstall)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
            // We're going away - no caller will be around to observe Exited
            batProcess?.Dispose();
            return null;
        }

        return batProcess;
    }

    /// <summary>
    /// Re-launch the running exe with the given args using <c>runas</c>, blocking until the elevated process exits.
    /// Mirrors the deleted ElevationService.TryInvokeElevatedAdminAction.
    /// </summary>
    private static InstallResult TryInvokeElevated(string arguments, string sourceExe)
    {
        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = sourceExe,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return new InstallResult(false, "Failed to start elevated process");
            p.WaitForExit();
            return p.ExitCode == 0
                ? new InstallResult(true)
                : new InstallResult(false, $"Elevated process exited with code {p.ExitCode}");
        }
        catch (Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x800704C7 || ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - user clicked No on the UAC prompt
            return new InstallResult(false, UserCancelled: true);
        }
        catch (Exception ex)
        {
            WPFLog.Log($"InstallationService.TryInvokeElevated: {ex}");
            return new InstallResult(false, ex.Message);
        }
    }

}
