using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using VolumeTrayAppWPF.Models;

namespace VolumeTrayAppWPF.Services;

/// <summary>
/// Immutable description of a release detected on GitHub. Built once per successful poll;
/// shared by reference to listeners. <see cref="Version"/> is the integer parsed from the tag name
/// (non-digit characters stripped) and is what we compare against <see cref="BuildInfo.BuildNumber"/>.
/// </summary>
public sealed record UpdateInfo(
    int Version,
    string TagName,
    string ReleaseName,
    string Changelog,
    string AssetUrl,
    long AssetSize);

/// <summary>
/// Background poller that asks GitHub for the latest non-prerelease of alchemyyy/VolumeTrayAppWPF,
/// compares it to <see cref="BuildInfo.BuildNumber"/>, and exposes the result for live UI binding.
/// One instance lives for the process; the loop self-cancels when
/// <see cref="AppSettings.CheckForUpdatesEnabled"/> flips off and resumes when it flips back on,
/// using the same interval value the user can edit through <see cref="AppSettings.UpdateCheckIntervalMs"/>.
/// All <see cref="StateChanged"/> notifications are marshalled to the UI thread the service was
/// constructed on, so subscribers don't need to dispatch themselves.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    // GitHub API basics. The "latest" endpoint already filters out prereleases and drafts server-side,
    // so we never have to scan the full release list. User-Agent is required by the API or it
    // returns 403.
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/alchemyyy/VolumeTrayAppWPF/releases/latest";
    private const string UserAgent = "VolumeTrayAppWPF-Updater";
    // Name of the single asset we expect attached to each release. The user has guaranteed this name
    // is stable so we can hard-match it instead of guessing.
    private const string AssetName = "VolumeTrayAppWPF.exe";

    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly HttpClient _http;

    // Single long-lived background loop. _loopCts is replaced whenever Start/Stop/Restart is called;
    // a token from the prior loop is honored by any in-flight Delay and the loop simply exits.
    private CancellationTokenSource? _loopCts;
    // Manual trigger from "Check for updates": setting this wakes the current Delay so the user
    // doesn't have to wait for the periodic tick.
    private TaskCompletionSource? _manualKick;

    private UpdateInfo? _available;
    private DateTime? _lastCheckTimeUtc;
    private bool _isChecking;
    private bool _disposed;

    /// <summary>Fired on the UI thread after any of AvailableUpdate, LastCheckTimeUtc, or IsChecking changes.</summary>
    public event Action? StateChanged;

    /// <summary>Latest detected update strictly newer than the running build, or null if up to date / never checked.</summary>
    public UpdateInfo? AvailableUpdate => _available;

    /// <summary>UTC timestamp of the last completed poll (success or failure), or null if never polled.</summary>
    public DateTime? LastCheckTimeUtc => _lastCheckTimeUtc;

    /// <summary>True while a poll or download is in flight.</summary>
    public bool IsChecking => _isChecking;

    public UpdateCheckService(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Single HttpClient for the lifetime of the service. GitHub demands UA; Accept set to the
        // documented v3 type so the response shape is stable across future API revisions.
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeConstants.UpdateNetworkTimeoutMs) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Kicks off the background loop. Safe to call multiple times; subsequent calls cancel the old
    /// loop's wait and start a fresh one. The loop respects <see cref="AppSettings.CheckForUpdatesEnabled"/>
    /// at every iteration: when the toggle is off, the loop idles on a long delay until the next Restart.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;

        Stop();
        CancellationTokenSource cts = new();
        _loopCts = cts;
        _ = Task.Run(() => RunLoopAsync(cts.Token));
    }

    /// <summary>
    /// Cancels the running loop. The in-flight HTTP call is allowed to finish (cancellation only
    /// hits the next Delay), so a partial download isn't aborted mid-flight.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts = _loopCts;
        _loopCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch { /* ignore */ }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Triggers an immediate poll. Returns the UpdateInfo (or null when up to date) once the poll
    /// completes. Resets the periodic-poll timer so the next automatic tick is one full interval
    /// from now, matching the user-visible "Check for updates resets the timer" behavior.
    /// </summary>
    public async Task<UpdateInfo?> CheckNowAsync()
    {
        if (_disposed) return _available;

        // Wake the running loop's Delay so it re-enters the poll branch immediately.
        // If no loop is running (Start never called or service stopped) run a one-shot poll inline.
        TaskCompletionSource? kick = _manualKick;
        if (kick != null)
        {
            kick.TrySetResult();
            // The loop will perform the poll; wait for IsChecking to flip back to false.
            await WaitForCheckToCompleteAsync();
            return _available;
        }

        await PollOnceAsync(CancellationToken.None);
        return _available;
    }

    private async Task WaitForCheckToCompleteAsync()
    {
        // Spin-and-yield: poll the IsChecking flag every 50ms with a 30s cap.
        // Used only as the awaiter for a CheckNowAsync that delegated work to the running loop;
        // we accept the brief wakeups in exchange for not maintaining a second completion channel.
        for (int i = 0; i < 600; i++)
        {
            await Task.Delay(50);
            if (!_isChecking) return;
        }
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        // Initial delay so the very first check doesn't fight with startup work for the UI/network.
        try { await Task.Delay(TimeConstants.UpdateCheckStartupDelayMs, token); }
        catch (OperationCanceledException) { return; }

        while (!token.IsCancellationRequested)
        {
            if (_settings.CheckForUpdatesEnabled) await PollOnceAsync(token);

            int intervalMs = NormalizedInterval(_settings.UpdateCheckIntervalMs);
            _manualKick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Task kickTask = _manualKick.Task;
                Task delayTask = Task.Delay(intervalMs, token);
                await Task.WhenAny(kickTask, delayTask);
            }
            catch (OperationCanceledException) { return; }
            finally
            {
                _manualKick = null;
            }
        }
    }

    private static int NormalizedInterval(int requested)
    {
        if (requested < TimeConstants.UpdateCheckIntervalMinMs) return TimeConstants.UpdateCheckIntervalMinMs;
        if (requested > TimeConstants.UpdateCheckIntervalMaxMs) return TimeConstants.UpdateCheckIntervalMaxMs;
        return requested;
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        SetChecking(true);
        try
        {
            UpdateInfo? info = await FetchLatestAsync(token);
            UpdateInfo? newer = info != null && info.Version > BuildInfo.BuildNumber ? info : null;
            _dispatcher.Invoke(() =>
            {
                _available = newer;
                _lastCheckTimeUtc = DateTime.UtcNow;
            });
        }
        catch (Exception ex)
        {
            // Network errors are expected: offline laptops, GitHub hiccups, captive portals.
            // Don't clear AvailableUpdate so a previously-detected update remains actionable
            // through transient outages; only update the timestamp so "Version stale" eventually trips.
            WPFLog.Log($"UpdateCheckService.PollOnceAsync: {ex.Message}");
            _dispatcher.Invoke(() => _lastCheckTimeUtc = DateTime.UtcNow);
        }
        finally
        {
            SetChecking(false);
        }
    }

    private void SetChecking(bool value)
    {
        _dispatcher.Invoke(() =>
        {
            if (_isChecking == value) return;
            _isChecking = value;
            StateChanged?.Invoke();
        });

        // The Invoke above already raised StateChanged for the IsChecking flip; raise once more
        // to cover the available-update / last-check-time fields we mutated either side of it.
        if (!value) _dispatcher.Invoke(() => StateChanged?.Invoke());
    }

    private async Task<UpdateInfo?> FetchLatestAsync(CancellationToken token)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, ReleasesApiUrl);
        using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token);
        resp.EnsureSuccessStatusCode();

        Stream stream = await resp.Content.ReadAsStreamAsync(token);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        JsonElement root = doc.RootElement;

        // Defense: even though "/releases/latest" filters prereleases server-side, the response shape
        // still carries the booleans. A future API change that broke that filter would otherwise leak
        // a pre-release through to "available update" silently.
        if (root.TryGetProperty("prerelease", out JsonElement preRel) && preRel.GetBoolean()) return null;
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()) return null;

        string tag = root.TryGetProperty("tag_name", out JsonElement tagEl) ? tagEl.GetString() ?? "" : "";
        string name = root.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
        string body = root.TryGetProperty("body", out JsonElement bodyEl) ? bodyEl.GetString() ?? "" : "";

        int version = ParseVersionFromTag(tag);
        if (version <= 0) return null;

        string? assetUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string aname = asset.TryGetProperty("name", out JsonElement an) ? an.GetString() ?? "" : "";
                if (!string.Equals(aname, AssetName, StringComparison.OrdinalIgnoreCase)) continue;

                assetUrl = asset.TryGetProperty("browser_download_url", out JsonElement url) ? url.GetString() : null;
                assetSize = asset.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : 0;
                break;
            }
        }

        if (string.IsNullOrEmpty(assetUrl)) return null;

        // Fall back to the tag if the release has no human-readable name set.
        string displayName = string.IsNullOrWhiteSpace(name) ? tag : name;
        return new UpdateInfo(version, tag, displayName, body, assetUrl!, assetSize);
    }

    /// <summary>
    /// Pulls the integer build number out of a tag name. Strips every non-digit character
    /// (so "v110", "release-110", and "110" all resolve to 110) and parses what remains.
    /// Returns 0 if no digits are present so the caller treats it as "no update".
    /// </summary>
    private static int ParseVersionFromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;

        StringBuilder digits = new(tag.Length);
        foreach (char c in tag)
        {
            if (char.IsDigit(c)) digits.Append(c);
        }
        if (digits.Length == 0) return 0;
        return int.TryParse(digits.ToString(), out int v) ? v : 0;
    }

    /// <summary>
    /// Downloads the staged exe into %LOCALAPPDATA%\Temp, writes a self-deleting BAT that waits for
    /// the running process to exit, moves the new exe over the current one, then relaunches.
    /// Returns true if staging + BAT launch succeeded; the caller is then expected to call Application.Shutdown
    /// (or equivalent) so the BAT's wait loop can proceed. False on any failure - the caller stays running.
    /// </summary>
    public async Task<bool> DownloadAndStageAsync(UpdateInfo info, CancellationToken token = default)
    {
        if (_disposed) return false;

        SetChecking(true);
        try
        {
            string tempRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            Directory.CreateDirectory(tempRoot);

            string stagedExe = Path.Combine(
                tempRoot, $"VolumeTrayAppWPF_update_{info.Version}_{Guid.NewGuid():N}.exe");
            string scriptPath = Path.Combine(
                tempRoot, $"VolumeTrayAppWPF_update_{info.Version}_{Guid.NewGuid():N}.bat");

            using (HttpResponseMessage resp = await _http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                resp.EnsureSuccessStatusCode();
                await using FileStream fs = new(stagedExe, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, token);
            }

            // Size sanity check. GitHub's asset metadata is the source of truth here; a mismatch usually
            // means a truncated download or a content-encoding mishap, and applying it would corrupt the install.
            FileInfo onDisk = new(stagedExe);
            if (info.AssetSize > 0 && onDisk.Length != info.AssetSize)
            {
                WPFLog.Log($"UpdateCheckService.DownloadAndStageAsync: size mismatch (got {onDisk.Length}, expected {info.AssetSize})");
                try { File.Delete(stagedExe); } catch { /* ignore */ }
                return false;
            }

            string currentExe = Process.GetCurrentProcess().MainModule?.FileName
                ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve current executable path");
            int currentPid = Environment.ProcessId;

            string scriptContents = BuildUpdateScript(currentPid, stagedExe, currentExe);
            await File.WriteAllTextAsync(scriptPath, scriptContents, Encoding.ASCII, token);

            ProcessStartInfo psi = new()
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = tempRoot,
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            WPFLog.Log($"UpdateCheckService.DownloadAndStageAsync: {ex.Message}");
            return false;
        }
        finally
        {
            SetChecking(false);
        }
    }

    /// <summary>
    /// BAT updater: polls the running PID until it disappears, moves the staged exe over the live one,
    /// relaunches, then self-deletes. Errors are swallowed; the worst case is a stale .bat lingering in Temp.
    /// Quoting is double-quoted throughout so paths with spaces (Program Files) survive intact.
    /// </summary>
    private static string BuildUpdateScript(int pid, string stagedExe, string currentExe)
    {
        StringBuilder sb = new();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set TARGETPID={pid}");
        sb.AppendLine(":waitloop");
        sb.AppendLine("tasklist /FI \"PID eq %TARGETPID%\" 2>NUL | find \"%TARGETPID%\" >NUL");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >NUL");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        // One extra second buffer so any file handles held by the dying process release before the move.
        sb.AppendLine("timeout /t 1 /nobreak >NUL");
        sb.AppendLine($"move /Y \"{stagedExe}\" \"{currentExe}\" >NUL");
        sb.AppendLine($"if errorlevel 1 goto cleanup");
        sb.AppendLine($"start \"\" \"{currentExe}\"");
        sb.AppendLine(":cleanup");
        sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _http.Dispose();
    }
}
