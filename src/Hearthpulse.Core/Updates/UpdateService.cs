using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

/// <summary>
/// Looks for a newer Hearthpulse release on GitHub, downloads the file this copy needs and installs a downloaded
/// Windows update once the app has closed, so a running .exe is never locked. Everything is best effort: no network
/// or an odd reply never throws; it is written to the log and reported by <see cref="CheckForUpdateAsync"/> so the
/// settings page can say why there is no update. A release file without a published SHA-256 is never installed.
/// </summary>
public static partial class UpdateService
{
    public const string RepoOwner = "Zulut30";

    public const string RepoName = "Hearthpulse_App";

    /// <summary>The Apps &amp; features entry the setup writes under HKCU (installer/windows/hearthpulse.nsi).</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Hearthpulse";

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
    public static UpdateInfo? ParseLatest(string? json, UpdateTarget target, string architecture, string linuxFlavor = "") =>
        ParseLatest(json, target, architecture, linuxFlavor, CurrentVersion);

    /// <summary><see cref="ParseLatest(string?, UpdateTarget, string, string)"/> for a given installed version (tests).</summary>
    internal static UpdateInfo? ParseLatest(string? json, UpdateTarget target, string architecture, string linuxFlavor, string installedVersion)
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
            var deltaName = target == UpdateTarget.WindowsInstalled ? DeltaUpdate.AssetName(installedVersion, version) : null;
            UpdateInfo? found = null;
            DeltaAsset? delta = null;
            string? sumsUrl = null;
            string? signatureUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || GetString(asset, "browser_download_url") is not { Length: > 0 } url)
                {
                    continue;
                }

                // GitHub publishes "sha256:<hex>" for every release file; the download is checked against it.
                var digest = GetString(asset, "digest");
                var sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71
                    ? digest[7..].ToLowerInvariant()
                    : null;
                var name = GetString(asset, "name");
                if (name == wanted)
                {
                    found = new UpdateInfo(version, wanted, url, sha256);
                }
                else if (name == deltaName && sha256 is not null)
                {
                    delta = new DeltaAsset(name, url, sha256);
                }
                else if (name == UpdateSignature.ChecksumsName)
                {
                    sumsUrl = url;
                }
                else if (name == UpdateSignature.SignatureName)
                {
                    signatureUrl = url;
                }
            }

            return found is null
                ? null
                : found with
                {
                    Delta = delta,
                    Notes = ReleaseNotes.ToPlainText(GetString(root, "body")),
                    PageUrl = ReleaseNotes.Page(GetString(root, "html_url")),
                    ChecksumsUrl = sumsUrl,
                    SignatureUrl = signatureUrl,
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Asks GitHub for the newest release; null when this copy is current or when anything goes wrong.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, CancellationToken cancellationToken = default) =>
        (await CheckForUpdateAsync(http, cancellationToken).ConfigureAwait(false)).Update;

    /// <summary>
    /// Asks GitHub for the newest release. Never throws: a failed request or an answer other than 200 is written to the
    /// log and comes back as <see cref="UpdateCheckOutcome.Failed"/>, not as "no update".
    /// </summary>
    public static Task<UpdateCheck> CheckForUpdateAsync(HttpClient http, CancellationToken cancellationToken = default) =>
        CheckForUpdateAsync(http, DetectTarget(), cancellationToken);

    /// <summary><see cref="CheckForUpdateAsync(HttpClient, CancellationToken)"/> for a given kind of copy (tests).</summary>
    /// <param name="repair">Offer the newest release even when it is not newer (a folder a failed delta left half-updated).</param>
    internal static Task<UpdateCheck> CheckForUpdateAsync(HttpClient http, UpdateTarget target, CancellationToken cancellationToken, bool repair = false) =>
        CheckForUpdateAsync(http, target, UpdateContext.Default(), cancellationToken, repair);

    /// <summary>
    /// The check with its state folder and clock passed in (the loop, tests). The last 200 answer is kept with its ETag
    /// in update-state/latest-release.json and sent back as If-None-Match, so an unchanged release costs GitHub nothing
    /// (a 304 does not count against the limit); a rate-limit answer says when to ask again (<see cref="UpdateCheck.RetryAfter"/>).
    /// </summary>
    internal static async Task<UpdateCheck> CheckForUpdateAsync(HttpClient http, UpdateTarget target, UpdateContext context,
        CancellationToken cancellationToken, bool repair = false)
    {
        try
        {
            if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse/" + CurrentVersion);
            }

            var cached = ReleaseCache.Read(context.StateDirectory);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (cached is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }

            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string json;
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
            {
                json = cached.Body;
            }
            else if (!response.IsSuccessStatusCode)
            {
                return FailedAnswer(response, context.Now());
            }
            else
            {
                json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                ReleaseCache.Write(context.StateDirectory, response.Headers.ETag?.ToString(), json);
            }

            var info = ParseLatest(json, target, RuntimeArchitecture(), LinuxFlavor());
            if (info is null && LatestTag(json) is { } tag && IsNewer(tag))
            {
                // Released, but the file for this platform is not uploaded yet (or was not built): not "up to date".
                Diag.Info($"Update check: {tag} has no {AssetName(tag, target, RuntimeArchitecture(), LinuxFlavor())} yet.");
                return UpdateCheck.Of(UpdateCheckOutcome.UpToDate, null, () => Loc.F("Core.Update.NoFileYet", tag));
            }

            if (info is null || (!IsNewer(info.Version) && !repair))
            {
                return UpdateCheck.Of(UpdateCheckOutcome.UpToDate, null, () => Loc.F("Core.Update.UpToDate", CurrentVersion));
            }

            Diag.Info($"Update available: {info.Version} ({info.AssetName}){(info.Sha256 is null ? ", no SHA-256 published" : string.Empty)}.");
            return UpdateCheck.Of(UpdateCheckOutcome.Available, info,
                () => info.Sha256 is null ? UnverifiedMessage(info) : Loc.F("Core.Update.Available", info.Version));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Diag.Info($"Update check skipped: {ex.Message}");
            var reason = ex.Message;
            return UpdateCheck.Of(UpdateCheckOutcome.Failed, null, () => Loc.F("Core.Update.Failed", reason));
        }
    }

    /// <summary>
    /// An answer other than 200/304, as a failed check. 403 with no requests left (or with Retry-After) and 429 are
    /// GitHub's limit: the check carries the moment GitHub allows the next request and says it.
    /// </summary>
    private static UpdateCheck FailedAnswer(HttpResponseMessage response, DateTimeOffset now)
    {
        var status = (int)response.StatusCode;
        var limited = status == 429
            || (status == 403 && ((response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0")
                || response.Headers.RetryAfter is not null));
        // 404: the repository has no published release yet (or is private); 403/429: GitHub's rate limit.
        var reason = status switch
        {
            404 => "no published release",
            _ when limited => "rate limited",
            403 => "forbidden",
            _ => response.ReasonPhrase ?? "error",
        };
        Diag.Info($"Update check: GitHub answered {status} ({reason}) for {LatestApi}.");
        string Message() => Loc.F("Core.Update.GitHubFailed", status, reason switch
        {
            "no published release" => Loc.T("Core.Update.NoRelease"),
            "rate limited" => Loc.T("Core.Update.RateLimited"),
            "forbidden" => Loc.T("Core.Update.Forbidden"),
            _ => reason,
        });
        if (!limited)
        {
            return UpdateCheck.Of(UpdateCheckOutcome.Failed, null, Message);
        }

        var retryAfter = RateLimitEnd(response, now);
        Diag.Info($"Update check: GitHub's limit allows the next request at {retryAfter.ToString("u", CultureInfo.InvariantCulture)}.");
        return UpdateCheck.Of(UpdateCheckOutcome.Failed, null, () => Message() + " " + TryLaterText(retryAfter)) with { RetryAfter = retryAfter };
    }

    /// <summary>The latest a limit reply can push the next check: a far-off or bogus reset never stops the checks for long.</summary>
    internal static readonly TimeSpan LongestLimit = TimeSpan.FromHours(6);

    /// <summary>
    /// When GitHub takes requests again: X-RateLimit-Reset (unix seconds), Retry-After (seconds or a date), else in an
    /// hour; never later than <see cref="LongestLimit"/> from now.
    /// </summary>
    private static DateTimeOffset RateLimitEnd(HttpResponseMessage response, DateTimeOffset now)
    {
        var end = ReportedLimitEnd(response, now);
        return end - now > LongestLimit ? now + LongestLimit : end;
    }

    private static DateTimeOffset ReportedLimitEnd(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0 && seconds < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return at > now ? at : now.AddMinutes(1);
        }

        return response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => now + delta,
            { Date: { } date } when date > now => date,
            _ => now.AddHours(1),
        };
    }

    /// <summary>"GitHub takes the next check after 14:05." in the player's local time.</summary>
    internal static string TryLaterText(DateTimeOffset at) =>
        Loc.F("Core.Update.TryLaterAt", at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));

    /// <summary>Why a release without a published checksum is not installed, for the log and the settings page.</summary>
    public static string UnverifiedMessage(UpdateInfo info) =>
        Loc.F("Core.Update.Unverified", info.Version, info.AssetName);

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
