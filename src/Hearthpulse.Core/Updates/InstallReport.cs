using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

/// <summary>What became of the install the previous run started (<see cref="UpdateService.TakeInstallReport"/>).</summary>
/// <param name="Version">The version that was being installed.</param>
/// <param name="Succeeded">This run is that version or newer.</param>
/// <param name="ExitCode">The setup's last exit code from the helper's result file; null when there is none (the PC was
/// switched off mid-install, the helper never ran, a delta install).</param>
/// <param name="FailedAttempts">Failed installs of this version so far (after two, it is left to a manual install).</param>
/// <param name="RolledBack">The helper put the app folder back from its backup after the setup failed twice.</param>
public sealed record InstallReport(string Version, bool Succeeded, int? ExitCode, int FailedAttempts, bool RolledBack)
{
    /// <summary>Core.Update.InstallFailed / InstallFailedManual text for a failure; empty when it succeeded.</summary>
    public string Message() =>
        Succeeded ? string.Empty
        : FailedAttempts >= InstallState.MaxAttempts ? Loc.F("Core.Update.InstallFailedManual", Version)
        : Loc.F("Core.Update.InstallFailed", Version);
}

/// <summary>
/// The install bookkeeping in &lt;data&gt;/update-state: pending-install.json written right before the helper starts,
/// install-result.txt the helper writes (exit=, attempts=, backup=, restored=), install-failures.json counting failed
/// installs per version. The outcome is decided at the next start by the running version, so a PC switched off
/// mid-install or a helper that died counts as a failure too; the exit code is only a detail.
/// </summary>
internal static class InstallState
{
    /// <summary>Failed installs of one version before it is left to a manual install.</summary>
    public const int MaxAttempts = 2;

    public const string PendingFile = "pending-install.json";

    public const string ResultFile = "install-result.txt";

    public const string FailuresFile = "install-failures.json";

    /// <summary>The copy of the app folder the helper makes before the setup runs; not ".update-old" (the delta's mark of a broken folder).</summary>
    public static string BackupDirectory(string appDirectory) =>
        appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".setup-backup";

    /// <summary>
    /// How long after the install started a start with no result file and no helper to ask (a delta) still waits
    /// before calling it a failure: the delta helper starts the app again itself, before its folder is settled.
    /// </summary>
    public static readonly TimeSpan JudgeAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Records the install about to start; the old result file goes so the next start does not read it.
    /// <paramref name="helper"/> is the helper process once it runs (written again then).
    /// </summary>
    public static void WritePending(string stateDirectory, string version, string fromVersion, string asset, DateTimeOffset now,
        (int Pid, DateTimeOffset Started)? helper = null)
    {
        Directory.CreateDirectory(stateDirectory);
        ReleaseCache.TryDelete(Path.Combine(stateDirectory, ResultFile));
        var values = new Dictionary<string, object>
        {
            ["version"] = version,
            ["fromVersion"] = fromVersion,
            ["asset"] = asset,
            ["startedUtc"] = now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        };
        if (helper is { } running)
        {
            values["helperPid"] = running.Pid;
            values["helperStartedUtc"] = running.Started.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }

        File.WriteAllText(Path.Combine(stateDirectory, PendingFile), JsonSerializer.Serialize(values));
    }

    /// <summary>True while the process <paramref name="pid"/> runs and is the one started at <paramref name="started"/> (not a reused id).</summary>
    public static bool HelperRunning(int pid, DateTimeOffset started)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited && (process.StartTime.ToUniversalTime() - started.UtcDateTime).Duration() < TimeSpan.FromSeconds(5);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Forgets an install that never started (the helper could not be launched).</summary>
    public static void ClearPending(string stateDirectory) => ReleaseCache.TryDelete(Path.Combine(stateDirectory, PendingFile));

    /// <summary>Failed installs of <paramref name="version"/> recorded so far.</summary>
    public static int FailedAttempts(string stateDirectory, string version) =>
        ReadFailures(stateDirectory).TryGetValue(version, out var count) ? count : 0;

    /// <summary>
    /// Reads and clears the marker: null without one; succeeded when <paramref name="installedVersion"/> is the
    /// version or newer (the backup folder is removed); otherwise failed, with the helper's result and one more failed
    /// attempt counted. The staged file is left alone, so the install is tried again at the next exit. While the
    /// helper may still be at work (no result file yet and its process runs, or, with no process to ask, less than
    /// <see cref="JudgeAfter"/> since the start) nothing is judged or cleared: null, and the next start decides.
    /// </summary>
    public static InstallReport? Take(string stateDirectory, string installedVersion, string appDirectory, DateTimeOffset now,
        Func<int, DateTimeOffset, bool> helperRunning)
    {
        var pendingPath = Path.Combine(stateDirectory, PendingFile);
        var resultPath = Path.Combine(stateDirectory, ResultFile);
        string? version = null;
        DateTimeOffset? started = null;
        (int Pid, DateTimeOffset Started)? helper = null;
        try
        {
            if (!File.Exists(pendingPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(pendingPath));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                version = Text(root, "version");
                started = Time(Text(root, "startedUtc"));
                if (root.TryGetProperty("helperPid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id)
                    && Time(Text(root, "helperStartedUtc")) is { } helperStarted)
                {
                    helper = (id, helperStarted);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update install marker could not be read ({ex.Message}).");
        }

        var reached = !string.IsNullOrEmpty(version) && UpdateService.CompareVersions(installedVersion, version) >= 0;
        if (!reached && !string.IsNullOrEmpty(version) && !File.Exists(resultPath)
            && (helper is { } h ? helperRunning(h.Pid, h.Started) : started is { } at && now - at < JudgeAfter))
        {
            Diag.Info($"Update {version}: the install helper may still be at work; the outcome is decided at a later start.");
            return null;
        }

        var result = ReadResult(resultPath);
        ReleaseCache.TryDelete(pendingPath);
        ReleaseCache.TryDelete(resultPath);
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        var failures = ReadFailures(stateDirectory);
        if (UpdateService.CompareVersions(installedVersion, version) >= 0)
        {
            DeleteBackup(appDirectory);
            foreach (var done in failures.Keys.Where(v => UpdateService.CompareVersions(v, installedVersion) <= 0).ToList())
            {
                failures.Remove(done);
            }

            WriteFailures(stateDirectory, failures);
            Diag.Info($"Update {version} installed.");
            return new InstallReport(version, Succeeded: true, result.Exit, failures.GetValueOrDefault(version), RolledBack: false);
        }

        var attempts = failures.GetValueOrDefault(version) + 1;
        failures[version] = attempts;
        WriteFailures(stateDirectory, failures);
        Diag.Info($"Update {version} did not install (still {installedVersion}): exit={result.ExitText ?? "none"}, " +
                  $"attempts={result.Attempts?.ToString(CultureInfo.InvariantCulture) ?? "?"}, backup={result.Backup}, restored={result.Restored}; failed installs of {version}: {attempts}.");
        return new InstallReport(version, Succeeded: false, result.Exit, attempts, result.Restored);
    }

    /// <param name="ExitText">The exit line as written: a number, or "hash_mismatch" when the setup was not run.</param>
    private readonly record struct Result(int? Exit, string? ExitText, int? Attempts, bool Backup, bool Restored);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static Result ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(path))
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }

            return new Result(Number("exit"), values.GetValueOrDefault("exit"), Number("attempts"), values.GetValueOrDefault("backup") == "1",
                values.GetValueOrDefault("restored") == "1");

            int? Number(string key) =>
                values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update install result could not be read ({ex.Message}).");
            return default;
        }
    }

    private static Dictionary<string, int> ReadFailures(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, FailuresFile);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) is { } counts)
            {
                return counts;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Diag.Info($"Update install failures could not be read ({ex.Message}).");
        }

        return [];
    }

    private static void WriteFailures(string stateDirectory, Dictionary<string, int> failures)
    {
        var path = Path.Combine(stateDirectory, FailuresFile);
        try
        {
            if (failures.Count == 0)
            {
                ReleaseCache.TryDelete(path);
                return;
            }

            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(failures));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update install failures could not be saved ({ex.Message}).");
        }
    }

    private static void DeleteBackup(string appDirectory)
    {
        var backup = BackupDirectory(appDirectory);
        try
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update backup {backup} could not be deleted ({ex.Message}).");
        }
    }
}
