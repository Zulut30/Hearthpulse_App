using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

/// <summary>How this copy of Hearthpulse is replaced when a newer release is out.</summary>
public enum UpdateTarget
{
    /// <summary>The Windows setup wizard installed this copy: run the new setup silently.</summary>
    WindowsInstalled,

    /// <summary>A Windows zip copy: unpack the new preview zip over the app's own folder.</summary>
    WindowsPortable,

    /// <summary>macOS: download the .pkg and hand it to the installer.</summary>
    MacPackage,

    /// <summary>Linux: download the package that fits this distribution and hand it to the installer.</summary>
    LinuxPackage,
}

/// <summary>A newer release than the running one, together with the file this platform needs.</summary>
/// <param name="Sha256">The file's SHA-256 as GitHub publishes it (lower-case hex), or null when the release has none.</param>
public sealed record UpdateInfo(string Version, string AssetName, string DownloadUrl, string PageUrl, string? Sha256 = null);

/// <summary>What an update check found.</summary>
public enum UpdateCheckOutcome
{
    /// <summary>This copy is the newest published release.</summary>
    UpToDate,

    /// <summary>A newer release with a file for this platform is out (<see cref="UpdateCheck.Update"/>).</summary>
    Available,

    /// <summary>GitHub could not be asked or gave an answer other than a release (see the message).</summary>
    Failed,
}

/// <summary>The result of one update check, with a sentence for the settings page.</summary>
public sealed record UpdateCheck(UpdateCheckOutcome Outcome, UpdateInfo? Update, string Message);

/// <summary>
/// Looks for a newer Hearthpulse release on GitHub, downloads the file this copy needs and installs a downloaded
/// Windows update once the app has closed, so a running .exe is never locked. Everything is best effort: no network
/// or an odd reply never throws; it is written to the log and reported by <see cref="CheckForUpdateAsync"/> so the
/// settings page can say why there is no update. A release file without a published SHA-256 is never installed.
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "Zulut30";

    public const string RepoName = "Hearthpulse_App";

    /// <summary>Where the newest published release is described. Drafts and pre-releases are never returned.</summary>
    public static string LatestApi => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>The running version (the Version property of every project), e.g. "0.4.0".</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Compares dotted versions, ignoring a leading "v" and any "-" or "+" suffix: older -1, same 0, newer 1.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var left = VersionParts(a);
        var right = VersionParts(b);
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return left[i] < right[i] ? -1 : 1;
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is a version the player does not have yet.</summary>
    public static bool IsNewer(string? candidate) => CompareVersions(candidate, CurrentVersion) > 0;

    /// <summary>The release file for a platform, matching the names the release workflow uploads.</summary>
    public static string AssetName(string version, UpdateTarget target, string architecture, string linuxFlavor = "") => target switch
    {
        UpdateTarget.WindowsInstalled => $"Hearthpulse-Setup-{version}-win-x64.exe",
        UpdateTarget.WindowsPortable => $"Hearthpulse-Preview-{version}-win-x64.zip",
        UpdateTarget.MacPackage when IsArm(architecture) => $"Hearthpulse-{version}-macOS-arm64.pkg",
        UpdateTarget.MacPackage => $"Hearthpulse-{version}-macOS-x64.pkg",
        UpdateTarget.LinuxPackage when linuxFlavor == "deb" => $"hearthpulse_{version}_{(IsArm(architecture) ? "arm64" : "amd64")}.deb",
        UpdateTarget.LinuxPackage when linuxFlavor == "rpm" => $"hearthpulse-{version}-1.{(IsArm(architecture) ? "aarch64" : "x86_64")}.rpm",
        _ => $"Hearthpulse-{version}-linux-{(IsArm(architecture) ? "arm64" : "x64")}.tar.gz",
    };

    /// <summary>How this copy is updated. An installed Windows copy runs the new setup; a zip copy unpacks over itself.</summary>
    public static UpdateTarget DetectTarget() =>
        OperatingSystem.IsWindows()
            ? (IsInstalledCopy() ? UpdateTarget.WindowsInstalled : UpdateTarget.WindowsPortable)
            : (OperatingSystem.IsMacOS() ? UpdateTarget.MacPackage : UpdateTarget.LinuxPackage);

    /// <summary>
    /// False for a copy the update for its target would not fit: the Windows zip is the cross-platform preview, so only a
    /// folder holding the preview (its Avalonia assembly) unpacks it; the Windows app run outside the folder its setup
    /// registered (moved, copied) is only told about the release.
    /// </summary>
    internal static bool InstallsItself(UpdateTarget target, string appDirectory, Func<string, bool> exists) =>
        target != UpdateTarget.WindowsPortable || exists(Path.Combine(appDirectory, "Avalonia.Base.dll"));

    /// <summary>
    /// "deb" or "rpm" when the package manager installed this copy, "" for everything else (the tar.gz install, a copy
    /// run from its folder). Going by the distribution alone offered a .deb to everyone who had unpacked the tar.gz on
    /// Ubuntu, and opening it installed a second copy next to theirs.
    /// </summary>
    public static string LinuxFlavor() =>
        OperatingSystem.IsLinux() ? LinuxFlavor(AppContext.BaseDirectory, File.Exists) : string.Empty;

    /// <summary><see cref="LinuxFlavor()"/> for an app folder, with the file check passed in (tests).</summary>
    internal static string LinuxFlavor(string appDirectory, Func<string, bool> exists)
    {
        // The .deb and the .rpm put the app in /opt/hearthpulse/app. "sudo ./install.sh" from the tar.gz uses the same
        // folder but writes its menu entry under /usr/local/share, the packages under /usr/share.
        if (!appDirectory.TrimEnd('/').Equals("/opt/hearthpulse/app", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (exists("/var/lib/dpkg/info/hearthpulse.list"))
        {
            return "deb";
        }

        return exists("/usr/share/applications/hearthpulse.desktop") ? "rpm" : string.Empty;
    }

    /// <summary>
    /// This copy only says that a new version is out and installs nothing: the portable packages, and an AppImage
    /// (a single file the player keeps wherever they like; no release package replaces it).
    /// </summary>
    public static bool NotifyOnly => AppPaths.IsPortable || IsAppImage;

    /// <summary>Running from an AppImage: its runtime sets APPIMAGE to the image file.</summary>
    public static bool IsAppImage => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 };

    /// <summary>"arm64" or "x64" for the running process.</summary>
    public static string RuntimeArchitecture() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>
    /// Reads the newest release reply and picks the file this platform needs. Null-tolerant on purpose: the GitHub
    /// payload carries nulls (a release with no assets, an asset with no name) that must not throw.
    /// </summary>
    public static UpdateInfo? ParseLatest(string? json, UpdateTarget target, string architecture, string linuxFlavor = "")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || GetBool(root, "draft") || GetBool(root, "prerelease"))
            {
                return null;
            }

            var version = GetString(root, "tag_name").TrimStart('v', 'V');
            if (version.Length == 0 || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var wanted = AssetName(version, target, architecture, linuxFlavor);
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind == JsonValueKind.Object
                    && GetString(asset, "name") == wanted
                    && GetString(asset, "browser_download_url") is { Length: > 0 } url)
                {
                    // GitHub publishes "sha256:<hex>" for every release file; the download is checked against it.
                    var digest = GetString(asset, "digest");
                    var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71
                        ? digest[7..].ToLowerInvariant()
                        : null;
                    return new UpdateInfo(version, wanted, url, GetString(root, "html_url"), sha256);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The update loop both apps run in the background: 20 seconds after it starts, then every six hours, asks GitHub
    /// for a newer release. One this copy must not install itself (<paramref name="notifyOnly"/>, or an update that would not
    /// fit it, <see cref="InstallsItself"/>) goes to
    /// <paramref name="available"/>, one downloaded and verified to <paramref name="staged"/>; either ends the loop. A
    /// failed check, an unverifiable file or a failed download goes to <paramref name="failed"/> as the text to show
    /// (read when shown, so it follows the language) and is tried again later. Callbacks run on a pool thread.
    /// </summary>
    public static Task WatchAsync(HttpClient http, bool notifyOnly, Action<UpdateInfo> available, Action<UpdateInfo> staged, Action<Func<string>> failed) =>
        WatchAsync(http, notifyOnly, available, staged, failed, TimeSpan.FromSeconds(20), DetectTarget(), AppContext.BaseDirectory, File.Exists);

    /// <summary>
    /// <see cref="WatchAsync(HttpClient, bool, Action{UpdateInfo}, Action{UpdateInfo}, Action{Func{string}})"/> with the
    /// first wait and this copy's target and folder passed in (tests). A copy the update would not fit
    /// (<see cref="InstallsItself"/>) is only told about it, as with <paramref name="notifyOnly"/>.
    /// </summary>
    internal static async Task WatchAsync(HttpClient http, bool notifyOnly, Action<UpdateInfo> available, Action<UpdateInfo> staged, Action<Func<string>> failed,
        TimeSpan firstWait, UpdateTarget target, string appDirectory, Func<string, bool> exists)
    {
        await Task.Delay(firstWait).ConfigureAwait(false);
        while (true)
        {
            try
            {
                var check = await CheckForUpdateAsync(http).ConfigureAwait(false);
                var info = check.Update;
                if (info is not null && (notifyOnly || !InstallsItself(target, appDirectory, exists)))
                {
                    available(info);
                    return;
                }

                if (info is { Sha256: not null } && await DownloadAsync(http, info).ConfigureAwait(false) is not null)
                {
                    staged(info);
                    return;
                }

                var message = check.Message;
                failed(info is { Sha256: not null } ? () => Loc.F("Host.Update.DownloadFailed", info.Version) : () => message);
            }
            catch (Exception ex)
            {
                Diag.Error("Update check failed", ex);
            }

            await Task.Delay(TimeSpan.FromHours(6)).ConfigureAwait(false);
        }
    }

    /// <summary>Asks GitHub for the newest release; null when this copy is current or when anything goes wrong.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken cancellationToken = default) =>
        (await CheckForUpdateAsync(http, cancellationToken).ConfigureAwait(false)).Update;

    /// <summary>
    /// Asks GitHub for the newest release. Never throws: a failed request or an answer other than 200 is written to the
    /// log and comes back as <see cref="UpdateCheckOutcome.Failed"/>, not as "no update".
    /// </summary>
    public static async Task<UpdateCheck> CheckForUpdateAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse/" + CurrentVersion);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // 404: the repository has no published release yet (or is private); 403/429: GitHub's rate limit.
                var reason = status switch
                {
                    404 => "no published release",
                    429 => "rate limited",
                    403 when response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0" => "rate limited",
                    403 => "forbidden",
                    _ => response.ReasonPhrase ?? "error",
                };
                Diag.Info($"Update check: GitHub answered {status} ({reason}) for {LatestApi}.");
                var shownReason = reason switch
                {
                    "no published release" => Loc.T("Core.Update.NoRelease"),
                    "rate limited" => Loc.T("Core.Update.RateLimited"),
                    "forbidden" => Loc.T("Core.Update.Forbidden"),
                    _ => reason,
                };
                return new UpdateCheck(UpdateCheckOutcome.Failed, null, Loc.F("Core.Update.GitHubFailed", status, shownReason));
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var info = ParseLatest(json, DetectTarget(), RuntimeArchitecture(), LinuxFlavor());
            if (info is null && LatestTag(json) is { } tag && IsNewer(tag))
            {
                // Released, but the file for this platform is not uploaded yet (or was not built): not "up to date".
                Diag.Info($"Update check: {tag} has no {AssetName(tag, DetectTarget(), RuntimeArchitecture(), LinuxFlavor())} yet.");
                return new UpdateCheck(UpdateCheckOutcome.UpToDate, null, Loc.F("Core.Update.NoFileYet", tag));
            }

            if (info is null || !IsNewer(info.Version))
            {
                return new UpdateCheck(UpdateCheckOutcome.UpToDate, null, Loc.F("Core.Update.UpToDate", CurrentVersion));
            }

            Diag.Info($"Update available: {info.Version} ({info.AssetName}){(info.Sha256 is null ? ", no SHA-256 published" : string.Empty)}.");
            return new UpdateCheck(UpdateCheckOutcome.Available, info,
                info.Sha256 is null ? UnverifiedMessage(info) : Loc.F("Core.Update.Available", info.Version));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Diag.Info($"Update check skipped: {ex.Message}");
            return new UpdateCheck(UpdateCheckOutcome.Failed, null, Loc.F("Core.Update.Failed", ex.Message));
        }
    }

    /// <summary>Why a release without a published checksum is not installed, for the log and the settings page.</summary>
    public static string UnverifiedMessage(UpdateInfo info) =>
        Loc.F("Core.Update.Unverified", info.Version, info.AssetName);

    /// <summary>Where a downloaded update waits until the app closes. Shared with the next run, so it is downloaded once.</summary>
    public static string StagedPath(UpdateInfo info) => Path.Combine(AppPaths.Root, "updates", info.AssetName);

    /// <summary>Downloads the release file; returns the staged path, or null when it could not be fetched.</summary>
    public static async Task<string?> DownloadAsync(HttpClient http, UpdateInfo info, CancellationToken cancellationToken = default)
    {
        // The setup runs silently, so a file that cannot be checked against the published one is never downloaded.
        if (info.Sha256 is null)
        {
            Diag.Info(UnverifiedMessage(info));
            // A copy an older build downloaded without a check would only take space (about 100 MB).
            TryDelete(StagedPath(info));
            return null;
        }

        try
        {
            var staged = StagedPath(info);
            var existing = new FileInfo(staged);
            if (existing.Exists && existing.Length > 0)
            {
                if (await MatchesDigestAsync(staged, info, cancellationToken).ConfigureAwait(false))
                {
                    return staged;
                }

                File.Delete(staged);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            DeleteOldDownloads(staged);
            var part = staged + ".part";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var sourceScope = source.ConfigureAwait(false);
                var file = File.Create(part);
                await using var fileScope = file.ConfigureAwait(false);
                await source.CopyToAsync(file, cts.Token).ConfigureAwait(false);
            }

            // The setup runs silently, so a file that is not exactly the published one is never kept.
            if (!await MatchesDigestAsync(part, info, cancellationToken).ConfigureAwait(false))
            {
                Diag.Info($"Update {info.Version} failed its checksum and was discarded.");
                File.Delete(part);
                return null;
            }

            File.Move(part, staged, overwrite: true);
            ClearDownloadMark(staged);
            Diag.Info($"Update {info.Version} downloaded ({info.AssetName}).");
            return staged;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Update download failed", ex);
            return null;
        }
    }

    /// <summary>True only when the file has the SHA-256 the release publishes; false when the release publishes none.</summary>
    private static async Task<bool> MatchesDigestAsync(string path, UpdateInfo info, CancellationToken cancellationToken)
    {
        if (info.Sha256 is null)
        {
            return false;
        }

        var stream = File.OpenRead(path);
        await using var streamScope = stream.ConfigureAwait(false);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == info.Sha256;
    }

    /// <summary>Earlier downloads are a hundred megabytes each and are never needed again.</summary>
    private static void DeleteOldDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(keep)!))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ = ex;
                }
            }
        }
    }

    /// <summary>
    /// Installs a downloaded Windows update. The helper script waits for this process to exit, so the file it replaces
    /// is never locked, then runs the setup silently (or unpacks the zip) and optionally starts the app again.
    /// Returns false when there is nothing to do (not Windows, nothing downloaded, unpack failed).
    /// </summary>
    public static bool ApplyStaged(UpdateInfo info, bool relaunch)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var staged = StagedPath(info);
        if (!File.Exists(staged) || !StagedFileMatches(info))
        {
            return false;
        }

        var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installer = DetectTarget() == UpdateTarget.WindowsInstalled || staged.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        string? extractDir = null;
        string? payloadDir = null;
        if (!installer)
        {
            extractDir = Path.Combine(Path.GetTempPath(), "hearthpulse-update");
            try
            {
                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, recursive: true);
                }

                ZipFile.ExtractToDirectory(staged, extractDir);
                payloadDir = PayloadDirectory(extractDir);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Diag.Error("Update could not be unpacked", ex);
                return false;
            }
        }

        var exe = Path.Combine(targetDir, "Hearthpulse.exe");
        var script = BuildApplyScript(
            Environment.ProcessId,
            staged,
            payloadDir ?? targetDir,
            targetDir,
            relaunch && File.Exists(exe) ? exe : null,
            installer,
            extractDir);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"hearthpulse-update-{Environment.ProcessId}.cmd");
        try
        {
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
            Diag.Info($"Update {info.Version} will install when Hearthpulse closes.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Diag.Error("Update helper could not start", ex);
            return false;
        }
    }

    /// <summary>
    /// The update helper: a small cmd script, because the installer may only touch the app's files after the app has
    /// closed. It waits for the process, installs, optionally starts the app again and deletes itself.
    /// </summary>
    public static string BuildApplyScript(int pid, string stagedFile, string sourceDir, string targetDir, string? relaunchExe, bool installer, string? cleanupDir)
    {
        // cmd expands %NAME% even inside quotes, so a "%" in a path is doubled.
        static string Cmd(string path) => path.Replace("%", "%%", StringComparison.Ordinal);
        stagedFile = Cmd(stagedFile);
        sourceDir = Cmd(sourceDir);
        targetDir = Cmd(targetDir);
        relaunchExe = relaunchExe is null ? null : Cmd(relaunchExe);
        cleanupDir = cleanupDir is null ? null : Cmd(cleanupDir);

        var lines = new List<string>
        {
            "@echo off",
            // The script is UTF-8; without this, cmd reads it in the OEM code page and a user name with accents
            // (C:\Users\Niccolò) breaks every path, so the update never installs.
            "chcp 65001 >nul",
            "rem Hearthpulse update helper: waits for the app to close, then installs the downloaded update.",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  ping -n 2 127.0.0.1 >nul",
            "  goto wait",
            ")",
        };

        if (installer)
        {
            lines.Add($"start \"\" /wait \"{stagedFile}\" /S");
        }
        else
        {
            lines.Add($"xcopy /E /Y /I \"{sourceDir}\\*\" \"{targetDir}\\\" >nul");
        }

        if (cleanupDir is not null && !string.Equals(cleanupDir, targetDir, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"rmdir /S /Q \"{cleanupDir}\"");
        }

        if (relaunchExe is not null)
        {
            lines.Add($"start \"\" \"{relaunchExe}\"");
        }

        lines.Add("del \"%~f0\"");
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Hands a downloaded package to the OS installer (macOS .pkg, Linux .deb/.rpm/.tar.gz).</summary>
    public static bool OpenInstaller(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path)?.Dispose();
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", path)?.Dispose();
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Error("Update file could not be opened", ex);
        }

        return false;
    }

    /// <summary>The folder inside a zip: the archive either holds the app directly or one folder that does.</summary>
    private static string PayloadDirectory(string extractDir)
    {
        var files = Directory.GetFiles(extractDir);
        var dirs = Directory.GetDirectories(extractDir);
        return files.Length == 0 && dirs.Length == 1 ? dirs[0] : extractDir;
    }

    /// <summary>Drops the "downloaded from the internet" mark so the update runs without a security prompt.</summary>
    private static void ClearDownloadMark(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.Delete(path + ":Zone.Identifier");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: the update still installs.
            _ = ex;
        }
    }

    /// <summary>True only when this copy sits in the folder the setup wizard recorded (see the uninstall key).</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsInstalledCopy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hearthpulse");
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
            {
                return false;
            }

            return string.Equals(Normalize(location), Normalize(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }

        static string Normalize(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }

    /// <summary>
    /// Checks the downloaded file again right before it is installed (hours can pass between download and close): a file
    /// that no longer has the published SHA-256 is deleted and not run.
    /// </summary>
    public static bool StagedFileMatches(UpdateInfo info)
    {
        var staged = StagedPath(info);
        try
        {
            if (info.Sha256 is not null && File.Exists(staged))
            {
                using var stream = File.OpenRead(staged);
                if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) == info.Sha256)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Update file could not be checked", ex);
            return false;
        }

        Diag.Info($"Update {info.Version} no longer matches its checksum and was not installed.");
        TryDelete(staged);
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Could not delete {Path.GetFileName(path)}", ex);
        }
    }

    /// <summary>The newest release's version from the reply, whatever files it has; null when there is none.</summary>
    private static string? LatestTag(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && !GetBool(root, "draft") && !GetBool(root, "prerelease")
                   && GetString(root, "tag_name").TrimStart('v', 'V') is { Length: > 0 } tag
                ? tag
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational is { Length: > 0 } ? informational : assembly.GetName().Version?.ToString(3);
        if (version is null or { Length: 0 })
        {
            return "0.0.0";
        }

        var cut = version.IndexOfAny(['-', '+']);
        var parsed = VersionParts(cut >= 0 ? version[..cut] : version);
        return $"{parsed[0]}.{parsed[1]}.{parsed[2]}";
    }

    private static int[] VersionParts(string? version)
    {
        var parts = new int[3];
        var text = (version ?? string.Empty).Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var index = 0;
        foreach (var piece in text.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (index == parts.Length)
            {
                break;
            }

            parts[index++] = int.TryParse(piece, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : 0;
        }

        return parts;
    }

    private static bool IsArm(string architecture) => architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
