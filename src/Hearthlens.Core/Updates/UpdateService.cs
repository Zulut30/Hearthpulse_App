using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Hearthlens.Core.Updates;

/// <summary>How this copy of Hearthlens is replaced when a newer release is out.</summary>
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

/// <summary>
/// Looks for a newer Hearthlens release on GitHub, downloads the file this copy needs and installs a downloaded
/// Windows update once the app has closed, so a running .exe is never locked. Everything is best effort: no network,
/// a missing file or an odd reply simply means "no update", never an error the player sees.
/// </summary>
public static class UpdateService
{
    public const string RepoOwner = "Dev-NotAqua";

    public const string RepoName = "HeartLens";

    /// <summary>Where the newest published release is described. Drafts and pre-releases are never returned.</summary>
    public static string LatestApi => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>The releases page, for the "what's new" link.</summary>
    public static string ReleasesPage => $"https://github.com/{RepoOwner}/{RepoName}/releases";

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
        UpdateTarget.WindowsInstalled => $"Hearthlens-Setup-{version}-win-x64.exe",
        UpdateTarget.WindowsPortable => $"Hearthlens-Preview-{version}-win-x64.zip",
        UpdateTarget.MacPackage when IsArm(architecture) => $"Hearthlens-{version}-macOS-arm64.pkg",
        UpdateTarget.MacPackage => $"Hearthlens-{version}-macOS-x64.pkg",
        UpdateTarget.LinuxPackage when linuxFlavor == "deb" => $"hearthlens_{version}_{(IsArm(architecture) ? "arm64" : "amd64")}.deb",
        UpdateTarget.LinuxPackage when linuxFlavor == "rpm" => $"hearthlens-{version}-1.{(IsArm(architecture) ? "aarch64" : "x86_64")}.rpm",
        _ => $"Hearthlens-{version}-linux-{(IsArm(architecture) ? "arm64" : "x64")}.tar.gz",
    };

    /// <summary>How this copy is updated. An installed Windows copy runs the new setup; a zip copy unpacks over itself.</summary>
    public static UpdateTarget DetectTarget() =>
        OperatingSystem.IsWindows()
            ? (IsInstalledCopy() ? UpdateTarget.WindowsInstalled : UpdateTarget.WindowsPortable)
            : (OperatingSystem.IsMacOS() ? UpdateTarget.MacPackage : UpdateTarget.LinuxPackage);

    /// <summary>"deb", "rpm" or "" (everything else), from the distro's own files.</summary>
    public static string LinuxFlavor()
    {
        if (!OperatingSystem.IsLinux())
        {
            return string.Empty;
        }

        if (File.Exists("/etc/debian_version"))
        {
            return "deb";
        }

        return File.Exists("/etc/redhat-release") || File.Exists("/etc/SuSE-release") || Directory.Exists("/etc/yum.repos.d")
            ? "rpm"
            : string.Empty;
    }

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

    /// <summary>Asks GitHub for the newest release; null when this copy is current or when anything goes wrong.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        try
        {
            if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthlens/" + CurrentVersion);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var info = ParseLatest(await response.Content.ReadAsStringAsync(cts.Token), DetectTarget(), RuntimeArchitecture(), LinuxFlavor());
            if (info is null || !IsNewer(info.Version))
            {
                return null;
            }

            Diag.Info($"Update available: {info.Version} ({info.AssetName}).");
            return info;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Diag.Info($"Update check skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>Where a downloaded update waits until the app closes. Shared with the next run, so it is downloaded once.</summary>
    public static string StagedPath(UpdateInfo info) => Path.Combine(AppPaths.Root, "updates", info.AssetName);

    /// <summary>Downloads the release file; returns the staged path, or null when it could not be fetched.</summary>
    public static async Task<string?> DownloadAsync(HttpClient http, UpdateInfo info, CancellationToken cancellationToken = default)
    {
        try
        {
            var staged = StagedPath(info);
            var existing = new FileInfo(staged);
            if (existing.Exists && existing.Length > 0)
            {
                if (await MatchesDigestAsync(staged, info, cancellationToken))
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
            using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
                await using var file = File.Create(part);
                await source.CopyToAsync(file, cts.Token);
            }

            // The setup runs silently, so a file that is not exactly the published one is never kept.
            if (!await MatchesDigestAsync(part, info, cancellationToken))
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

    /// <summary>True when the file has the SHA-256 the release publishes (or the release publishes none).</summary>
    private static async Task<bool> MatchesDigestAsync(string path, UpdateInfo info, CancellationToken cancellationToken)
    {
        if (info.Sha256 is null)
        {
            return true;
        }

        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
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
        if (!File.Exists(staged))
        {
            return false;
        }

        var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installer = DetectTarget() == UpdateTarget.WindowsInstalled || staged.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        string? extractDir = null;
        string? payloadDir = null;
        if (!installer)
        {
            extractDir = Path.Combine(Path.GetTempPath(), "hearthlens-update");
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

        var exe = Path.Combine(targetDir, "Hearthlens.exe");
        var script = BuildApplyScript(
            Environment.ProcessId,
            staged,
            payloadDir ?? targetDir,
            targetDir,
            relaunch && File.Exists(exe) ? exe : null,
            installer,
            extractDir);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"hearthlens-update-{Environment.ProcessId}.cmd");
        try
        {
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            Diag.Info($"Update {info.Version} will install when Hearthlens closes.");
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
        static string Cmd(string path) => path.Replace("%", "%%");
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
            "rem Hearthlens update helper: waits for the app to close, then installs the downloaded update.",
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
                Process.Start("open", path);
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", path);
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hearthlens");
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

            parts[index++] = int.TryParse(piece, out var number) && number > 0 ? number : 0;
        }

        return parts;
    }

    private static bool IsArm(string architecture) => architecture.Equals("arm64", StringComparison.OrdinalIgnoreCase);

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
