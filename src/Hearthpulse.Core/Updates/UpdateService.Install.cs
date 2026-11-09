using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

public static partial class UpdateService
{
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

        var targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var state = UpdateContext.Default().StateDirectory;
        if (info.Delta is not null && MarkPending(state, info) && DeltaUpdate.Apply(info, targetDir, relaunch))
        {
            return true;
        }

        var staged = StagedPath(info);
        if (!File.Exists(staged) || !StagedFileMatches(info))
        {
            return false;
        }

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
            extractDir,
            Path.Combine(state, InstallState.ResultFile));
        var scriptPath = Path.Combine(Path.GetTempPath(), $"hearthpulse-update-{Environment.ProcessId}.cmd");
        try
        {
            if (!MarkPending(state, info))
            {
                return false;
            }

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
            InstallState.ClearPending(state);
            return false;
        }
    }

    /// <summary>
    /// The outcome of the install the previous run started; null when none was pending. Reads and clears the marker.
    /// Call once at start: a failure counts one more failed attempt of that version.
    /// </summary>
    public static InstallReport? TakeInstallReport() =>
        InstallState.Take(UpdateContext.Default().StateDirectory, CurrentVersion, AppContext.BaseDirectory);

    /// <summary>Writes the install marker the next start reads; false (and logged) when it cannot be written.</summary>
    private static bool MarkPending(string stateDirectory, UpdateInfo info)
    {
        try
        {
            InstallState.WritePending(stateDirectory, info.Version, CurrentVersion, info.AssetName, DateTimeOffset.UtcNow);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a failed install would pass silently; the update is not started this time.
            Diag.Error("Update install marker could not be written", ex);
            return false;
        }
    }

    /// <summary>
    /// The update helper: a small cmd script, because the installer may only touch the app's files after the app has
    /// closed. It waits for the process (at most two minutes; then the setup closes a hung one itself), copies the app
    /// folder to &lt;app&gt;.setup-backup, runs the setup silently and once more after a few seconds when it fails, puts
    /// the folder back from the copy when the second run fails too, writes <paramref name="resultFile"/> (exit=,
    /// attempts=, backup=, restored=), optionally starts the app again and deletes itself. No delayed expansion, every
    /// path quoted, "%" doubled: paths with spaces, %, !, &amp; and any letters work.
    /// </summary>
    public static string BuildApplyScript(int pid, string stagedFile, string sourceDir, string targetDir, string? relaunchExe, bool installer, string? cleanupDir,
        string? resultFile = null) =>
        BuildApplyScript(pid, stagedFile, sourceDir, targetDir, relaunchExe, installer, cleanupDir, resultFile, waitSeconds: 120, retrySeconds: 5);

    /// <summary><see cref="BuildApplyScript(int, string, string, string, string?, bool, string?, string?)"/> with the waits passed in (tests).</summary>
    internal static string BuildApplyScript(int pid, string stagedFile, string sourceDir, string targetDir, string? relaunchExe, bool installer, string? cleanupDir,
        string? resultFile, int waitSeconds, int retrySeconds)
    {
        // cmd expands %NAME% even inside quotes, so a "%" in a path is doubled.
        static string Cmd(string path) => path.Replace("%", "%%", StringComparison.Ordinal);
        targetDir = targetDir.TrimEnd('\\', '/');
        var backupDir = Cmd(InstallState.BackupDirectory(targetDir));
        stagedFile = Cmd(stagedFile);
        sourceDir = Cmd(sourceDir);
        targetDir = Cmd(targetDir);
        relaunchExe = relaunchExe is null ? null : Cmd(relaunchExe);
        cleanupDir = cleanupDir is null ? null : Cmd(cleanupDir);
        const string Robocopy = "/MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul 2>nul";

        var lines = new List<string>
        {
            "@echo off",
            // The script is UTF-8; without this, cmd reads it in the OEM code page and a user name with accents
            // (C:\Users\Niccolò) breaks every path, so the update never installs.
            "chcp 65001 >nul",
            "rem Hearthpulse update helper: waits for the app to close, then installs the downloaded update.",
            "set /a waited=0",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \"{pid}\" >nul",
            "if errorlevel 1 goto closed",
            // A hung app does not block the update forever: the setup closes it (NSIS CloseApp).
            $"if %waited% GEQ {waitSeconds} goto closed",
            "ping -n 2 127.0.0.1 >nul",
            "set /a waited+=1",
            "goto wait",
            ":closed",
            "set code=0",
            "set attempts=1",
            "set backup=0",
            "set restored=0",
        };

        if (installer)
        {
            lines.AddRange(
            [
                // A copy of the app folder, put back if the setup fails twice. Without it the setup still runs.
                "set backup=1",
                $"robocopy \"{targetDir}\" \"{backupDir}\" {Robocopy}",
                "if errorlevel 8 set backup=0",
                $"if \"%backup%\"==\"0\" rmdir /S /Q \"{backupDir}\" 2>nul",
                $"start \"\" /b /wait \"{stagedFile}\" /S",
                "set code=%errorlevel%",
                "if \"%code%\"==\"0\" goto installed",
                $"ping -n {retrySeconds + 1} 127.0.0.1 >nul",
                "set attempts=2",
                $"start \"\" /b /wait \"{stagedFile}\" /S",
                "set code=%errorlevel%",
                "if \"%code%\"==\"0\" goto installed",
                "if \"%backup%\"==\"0\" goto report",
                $"robocopy \"{backupDir}\" \"{targetDir}\" {Robocopy}",
                "if not errorlevel 8 set restored=1",
                $"if \"%restored%\"==\"1\" rmdir /S /Q \"{backupDir}\"",
                "goto report",
                ":installed",
                $"if \"%backup%\"==\"1\" rmdir /S /Q \"{backupDir}\"",
            ]);
        }
        else
        {
            lines.Add($"xcopy /E /Y /I \"{sourceDir}\\*\" \"{targetDir}\\\" >nul");
            lines.Add("set code=%errorlevel%");
        }

        lines.Add(":report");
        if (resultFile is not null)
        {
            var resultDir = Cmd(Path.GetDirectoryName(resultFile) ?? ".");
            resultFile = Cmd(resultFile);
            lines.AddRange(
            [
                $"if not exist \"{resultDir}\" mkdir \"{resultDir}\"",
                "(",
                "echo exit=%code%",
                "echo attempts=%attempts%",
                "echo backup=%backup%",
                "echo restored=%restored%",
                $") > \"{resultFile}\"",
            ]);
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
}
