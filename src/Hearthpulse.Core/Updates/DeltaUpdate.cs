using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Updates;

/// <summary>A release file the installed Windows app can update itself from: name, address and published SHA-256.</summary>
public sealed record DeltaAsset(string Name, string DownloadUrl, string Sha256);

/// <summary>One file of the Windows app as a release manifest lists it: path relative to the app folder ("/" between folders).</summary>
public sealed record DeltaFile(string Path, long Size, string Sha256);

/// <summary>
/// What a delta package turns one version's app folder into: the files of <see cref="From"/> (<see cref="Base"/>, from
/// that release's manifest) and of <see cref="To"/> (<see cref="Target"/>). The package carries the files that differ.
/// </summary>
public sealed record DeltaPlan(string From, string To, IReadOnlyList<DeltaFile> Base, IReadOnlyList<DeltaFile> Target)
{
    /// <summary>Files of both versions whose content differs.</summary>
    public IReadOnlyList<DeltaFile> Replaced { get; } = Diff(Target, Base, sameHash: false, inOther: true);

    /// <summary>Files the new version has and the old one did not.</summary>
    public IReadOnlyList<DeltaFile> Added { get; } = Diff(Target, Base, sameHash: false, inOther: false);

    /// <summary>Files the old version had and the new one does not.</summary>
    public IReadOnlyList<DeltaFile> Removed { get; } = Diff(Base, Target, sameHash: false, inOther: false);

    /// <summary>Every file the package must carry: replaced and added.</summary>
    public IReadOnlyList<DeltaFile> Carried => [.. Replaced, .. Added];

    private static List<DeltaFile> Diff(IReadOnlyList<DeltaFile> files, IReadOnlyList<DeltaFile> other, bool sameHash, bool inOther)
    {
        var byPath = other.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        return [.. files.Where(f => byPath.TryGetValue(f.Path, out var o) == inOther && (!inOther || (o!.Sha256 == f.Sha256) == sameHash))];
    }
}

/// <summary>
/// Delta updates of the installed Windows app: instead of the whole setup (about 54 MB) it downloads only the files that
/// changed since the installed version (a few MB when the .NET runtime is the same). Taken only when the installed
/// copy is exactly the release the package starts from, file for file; anything else, and any failure on the way,
/// leaves the full setup to do the update, as before. Release side: installer/windows/delta.py.
/// </summary>
public static partial class DeltaUpdate
{
    /// <summary>The added files a delta brought, for the uninstaller (installer/windows/hearthpulse.nsi reads it).</summary>
    public const string UninstallExtraFile = "uninstall-extra.txt";

    private const string PlanEntry = "delta.json";

    /// <summary>SHA-256 of each plan this process prepared, by version (see <see cref="StagedPlan"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PreparedPlans = new(StringComparer.Ordinal);
    private const string FilesPrefix = "files/";

    /// <summary>The delta package from <paramref name="from"/> to <paramref name="to"/>, as the release publishes it.</summary>
    public static string AssetName(string from, string to) => $"Hearthpulse-Delta-{from}-{to}-win-x64.zip";

    /// <summary>The list of every file of a version with its size and SHA-256, as the release publishes it.</summary>
    public static string ManifestName(string version) => $"Hearthpulse-Files-{version}-win-x64.json";

    /// <summary>Where the verified files of a delta wait until the app closes. A folder: the setup download clears only
    /// the files of the updates folder (UpdateService.DeleteOldDownloads), never a delta in progress.</summary>
    public static string StagingDirectory(UpdateInfo info) => Path.Combine(AppPaths.Root, "updates", "delta", info.Version);

    /// <summary>Left when a delta to this version could not be installed: the next try takes the full setup.</summary>
    public static string FailedMark(UpdateInfo info) => Path.Combine(AppPaths.Root, "updates", "delta", info.Version + ".failed");

    /// <summary>
    /// Reads a package's plan; null when it is not one or names a path that could leave the app folder or that the
    /// helper script could not quote (only letters, digits, space and <c>. _ - + /</c> are allowed).
    /// </summary>
    public static DeltaPlan? ReadPlan(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var from = Text(root, "from");
            var to = Text(root, "to");
            var files = Files(root, "base");
            var target = Files(root, "target");
            return from.Length == 0 || to.Length == 0 || files is null || target is null ? null : new DeltaPlan(from, to, files, target);
        }
        catch (JsonException)
        {
            return null;
        }

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        static List<DeltaFile>? Files(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var files = new List<DeltaFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes < 0)
                {
                    return null;
                }

                var path = Text(item, "path");
                var sha = Text(item, "sha256");
                if (!IsSafePath(path) || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower) || !seen.Add(path))
                {
                    return null;
                }

                files.Add(new DeltaFile(path, bytes, sha));
            }

            return files;
        }
    }

    /// <summary>A relative path inside the app folder, made of characters the helper script uses as they are.</summary>
    public static bool IsSafePath(string path)
    {
        if (path.Length is 0 or > 200 || path[0] == '/' || path[^1] == '/')
        {
            return false;
        }

        foreach (var c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' or ' ' or '/'))
            {
                return false;
            }
        }

        // No "." or ".." (nor "..."), no trailing dot or space, no device names (CON, NUL, COM1.txt...): Windows would
        // read them differently. installer/windows/delta.py applies the same rule, so a release cannot publish one.
        return path.Split('/').All(part => part.Length > 0 && part.Trim() == part && !part.EndsWith('.') && !IsDeviceName(part));

        static bool IsDeviceName(string part)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
        }
    }

    /// <summary>
    /// A delta install that could not put every old file back left its backup next to the app folder: the folder mixes
    /// two versions, and the update loop installs the newest release with the full setup even when it is not newer.
    /// </summary>
    public static bool IsBroken(string appDirectory) => Directory.Exists(appDirectory.TrimEnd('\\', '/') + ".update-old");

    /// <summary>
    /// After a start: drops what an earlier delta left that is no longer needed - the copy folder next to the app, and
    /// staged files, packages and failure marks of versions up to the running one. Nothing while <see cref="IsBroken"/>:
    /// the backup and the mark wait for the full setup (which removes the backup itself).
    /// </summary>
    public static void CleanUp(string appDirectory, string runningVersion)
    {
        if (IsBroken(appDirectory))
        {
            return;
        }

        DeleteDirectory(appDirectory.TrimEnd('\\', '/') + ".update-new");
        var root = Path.Combine(AppPaths.Root, "updates", "delta");
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                // A package left by a crash is never needed (a new check downloads it again); the rest by version.
                var name = Path.GetFileName(path);
                var version = name.EndsWith(".failed", StringComparison.Ordinal) ? name[..^7] : name;
                if (name.StartsWith("Hearthpulse-Delta-", StringComparison.Ordinal) || UpdateService.CompareVersions(version, runningVersion) <= 0)
                {
                    DeleteDirectory(path);
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Old delta updates could not be removed", ex);
        }
    }

    /// <summary>True when every file of <paramref name="files"/> is in <paramref name="directory"/> with its size and SHA-256 (or only its size).</summary>
    public static bool FilesMatch(IEnumerable<DeltaFile> files, string directory, bool sizesOnly = false)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(directory, OsPath(file.Path));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size || (!sizesOnly && Sha256(path) != file.Sha256))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Downloads the delta package of <paramref name="info"/>, checks it against its published SHA-256, checks that
    /// <paramref name="appDirectory"/> holds exactly the files of the version it starts from (<paramref name="installedVersion"/>),
    /// and unpacks the files it carries into <see cref="StagingDirectory"/>, each checked against the new version's
    /// manifest. False on any failure, with nothing left behind: the full setup then does the update.
    /// </summary>
    public static async Task<bool> PrepareAsync(HttpClient http, UpdateInfo info, string appDirectory, string installedVersion,
        CancellationToken cancellationToken = default)
    {
        if (info.Delta is not { } delta || File.Exists(FailedMark(info)))
        {
            return false;
        }

        var staging = StagingDirectory(info);
        var zip = Path.Combine(AppPaths.Root, "updates", "delta", delta.Name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            if (!await DownloadAsync(http, delta, zip, cancellationToken).ConfigureAwait(false))
            {
                Diag.Info($"Delta update {info.Version}: the package did not download or failed its checksum.");
                return false;
            }

            var archive = await ZipFile.OpenReadAsync(zip, cancellationToken).ConfigureAwait(false);
            await using var archiveScope = archive.ConfigureAwait(false);
            var planJson = ReadEntry(archive, PlanEntry);
            var plan = ReadPlan(planJson);
            if (plan is null || plan.From != installedVersion || plan.To != info.Version)
            {
                Diag.Info($"Delta update {info.Version}: the package is not from {installedVersion} to {info.Version}.");
                return false;
            }

            // A file where the new version adds one is not the app's (the rollback would delete it): the setup handles that.
            if (!FilesMatch(plan.Base, appDirectory) || plan.Added.Any(f => File.Exists(Path.Combine(appDirectory, OsPath(f.Path)))))
            {
                Diag.Info($"Delta update {info.Version}: the installed files are not exactly those of {installedVersion}; the full setup updates.");
                return false;
            }

            DeleteDirectory(staging);
            var files = Path.Combine(staging, "files");
            foreach (var file in plan.Carried)
            {
                var entry = archive.GetEntry(FilesPrefix + file.Path);
                if (entry is null || entry.Length != file.Size)
                {
                    Diag.Info($"Delta update {info.Version}: the package lacks {file.Path}.");
                    DeleteDirectory(staging);
                    return false;
                }

                var path = Path.Combine(files, OsPath(file.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await entry.ExtractToFileAsync(path, overwrite: true, cancellationToken).ConfigureAwait(false);
            }

            if (!FilesMatch(plan.Carried, files))
            {
                Diag.Info($"Delta update {info.Version}: an unpacked file does not match the manifest of {info.Version}.");
                DeleteDirectory(staging);
                return false;
            }

            // The plan is kept with its SHA-256 here, in memory: the copy on disk is trusted at install time only if
            // it is still the one this process checked (the package itself was checked against its published digest).
            await File.WriteAllTextAsync(Path.Combine(staging, PlanEntry), planJson, cancellationToken).ConfigureAwait(false);
            PreparedPlans[info.Version] = Sha256Text(planJson!);
            Diag.Info($"Delta update {info.Version} ready: {plan.Replaced.Count} files replaced, {plan.Added.Count} added, {plan.Removed.Count} removed.");
            return true;
        }
        catch (Exception ex)
        {
            // Whatever went wrong (a full disk, a bad archive, an unexpected exception): nothing staged, the setup updates.
            Diag.Error("Delta update failed", ex);
            DeleteDirectory(staging);
            return false;
        }
        finally
        {
            TryDelete(zip);
            TryDelete(zip + ".part");
        }
    }

    /// <summary>
    /// The staged plan, checked again right before installing: the plan is the one this process prepared (its SHA-256
    /// was kept in memory), the staged files still have their hashes, and the installed files their sizes (their
    /// hashes were checked when the delta was prepared; at exit the whole folder is not read again). Null with nothing
    /// staged by this process; null and marked failed when anything changed.
    /// </summary>
    public static DeltaPlan? StagedPlan(UpdateInfo info, string appDirectory, string installedVersion)
    {
        var staging = StagingDirectory(info);
        var planPath = Path.Combine(staging, PlanEntry);
        if (!PreparedPlans.TryGetValue(info.Version, out var planHash) || !File.Exists(planPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(planPath);
            var plan = Sha256Text(json) == planHash ? ReadPlan(json) : null;
            if (plan is not null && plan.From == installedVersion && plan.To == info.Version
                && FilesMatch(plan.Carried, Path.Combine(staging, "files"))
                && FilesMatch(plan.Base, appDirectory, sizesOnly: true)
                && !plan.Added.Any(f => File.Exists(Path.Combine(appDirectory, OsPath(f.Path)))))
            {
                return plan;
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Delta update could not be checked", ex);
        }

        Diag.Info($"Delta update {info.Version} no longer matches; the full setup updates.");
        DeleteDirectory(staging);
        MarkFailed(info);
        return null;
    }

    /// <summary>Remembers that the delta to this version failed, so the next try downloads the full setup.</summary>
    public static void MarkFailed(UpdateInfo info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FailedMark(info))!);
            File.WriteAllText(FailedMark(info), "delta failed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Delta failure could not be recorded", ex);
        }
    }

    /// <summary>
    /// Installs a staged delta once this process has exited (see <see cref="BuildApplyScript"/>). False when nothing is
    /// staged or it no longer matches: the caller falls back to the full setup.
    /// </summary>
    public static bool Apply(UpdateInfo info, string appDirectory, bool relaunch)
    {
        var plan = StagedPlan(info, appDirectory, UpdateService.CurrentVersion);
        if (plan is null)
        {
            return false;
        }

        var exe = Path.Combine(appDirectory, "Hearthpulse.exe");
        var script = BuildApplyScript(Environment.ProcessId, Path.Combine(StagingDirectory(info), "files"), appDirectory, plan,
            relaunch ? exe : null, UpdateService.UninstallKey, FailedMark(info), StagingDirectory(info));
        var scriptPath = Path.Combine(Path.GetTempPath(), $"hearthpulse-delta-{Environment.ProcessId}.cmd");
        try
        {
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            })?.Dispose();
            Diag.Info($"Delta update {info.Version} will install when Hearthpulse closes.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Diag.Error("Delta update helper could not start", ex);
            return false;
        }
    }

    private static async Task<bool> DownloadAsync(HttpClient http, DeltaAsset delta, string path, CancellationToken cancellationToken)
    {
        var part = path + ".part";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(15));
        using (var response = await http.GetAsync(delta.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var sourceScope = source.ConfigureAwait(false);
            var file = File.Create(part);
            await using var fileScope = file.ConfigureAwait(false);
            await source.CopyToAsync(file, cts.Token).ConfigureAwait(false);
        }

        if (Sha256(part) != delta.Sha256)
        {
            return false;
        }

        File.Move(part, path, overwrite: true);
        return true;
    }

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sha256Text(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string OsPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ex;
        }
    }
}
