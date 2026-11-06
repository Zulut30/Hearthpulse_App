using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Updates;

public static partial class DeltaUpdate
{
    /// <summary>
    /// The delta helper: waits for the app to exit, copies the new files next to the app folder (nothing is touched if
    /// that fails), then swaps by renames, keeping every old file in a backup folder. Every copy and rename is tried
    /// <paramref name="tries"/> times a second apart (an antivirus or the search indexer may hold a file for a moment).
    /// A rename that still fails puts the old files back, records <paramref name="failedMark"/> (the next try takes the
    /// full setup) and starts the old app again; when an old file cannot be put back either, the backup is kept, the app
    /// is not started (its folder is a mix of two versions) and the next start repairs it with the full setup
    /// (<see cref="IsBroken"/>). On success it updates the version in Apps &amp; features and lists the added files for
    /// the uninstaller.
    /// </summary>
    public static string BuildApplyScript(int pid, string stagedFiles, string appDirectory, DeltaPlan plan, string? relaunchExe,
        string uninstallKey, string failedMark, string? cleanupDir, int tries = 5)
    {
        static string Cmd(string path) => path.Replace("%", "%%", StringComparison.Ordinal);
        static string Win(string path) => path.Replace('/', '\\');
        var app = Cmd(appDirectory.TrimEnd('\\', '/'));
        var staged = Cmd(stagedFiles.TrimEnd('\\', '/'));
        var fresh = app + ".update-new";
        var backup = app + ".update-old";
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            $"rem Hearthpulse delta update {plan.From} -> {plan.To}: waits for the app to close, then swaps the changed files.",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" /NH 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  ping -n 2 127.0.0.1 >nul",
            "  goto wait",
            ")",
            // A backup folder left by an earlier failed rollback may hold the only old copies: never overwrite it.
            $"if exist \"{backup}\" goto abort",
            $"if exist \"{fresh}\" rmdir /S /Q \"{fresh}\"",
            "set \"KEEP_BACKUP=\"",
        };

        // 1. Copy: the new files next to the app folder (same drive, so the swap is renames only).
        foreach (var dir in Folders(plan.Carried))
        {
            lines.Add($"mkdir \"{fresh}\\{dir}\" 2>nul");
        }

        lines.Add($"mkdir \"{fresh}\" 2>nul");
        var step = 0;
        foreach (var file in plan.Carried)
        {
            Retry($"copy /Y \"{staged}\\{Win(file.Path)}\" \"{fresh}\\{Win(file.Path)}\"", "goto abort");
        }

        // 2. Swap: old files into the backup, new files into place.
        lines.Add($"mkdir \"{backup}\" 2>nul");
        foreach (var dir in Folders([.. plan.Replaced, .. plan.Removed]))
        {
            lines.Add($"mkdir \"{backup}\\{dir}\" 2>nul");
        }

        foreach (var file in plan.Replaced.Concat(plan.Removed))
        {
            Retry($"move /Y \"{app}\\{Win(file.Path)}\" \"{backup}\\{Win(file.Path)}\"", "goto rollback");
        }

        foreach (var dir in Folders(plan.Added))
        {
            lines.Add($"mkdir \"{app}\\{dir}\" 2>nul");
        }

        foreach (var file in plan.Carried)
        {
            Retry($"move /Y \"{fresh}\\{Win(file.Path)}\" \"{app}\\{Win(file.Path)}\"", "goto rollback");
        }

        // 3. Done: the version Apps & features shows, the uninstaller's list, clean-up.
        lines.Add($"reg add \"HKCU\\{uninstallKey}\" /v DisplayVersion /t REG_SZ /d \"{plan.To}\" /f >nul 2>nul");
        foreach (var file in plan.Added)
        {
            lines.Add($">>\"{app}\\{UninstallExtraFile}\" echo {Win(file.Path)}");
        }

        var baseFolders = new HashSet<string>(Folders(plan.Base), StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Folders(plan.Added).Where(d => !baseFolders.Contains(d)).OrderByDescending(d => d.Count(c => c == '\\')))
        {
            lines.Add($">>\"{app}\\{UninstallExtraFile}\" echo {dir}\\");
        }

        lines.Add($"rmdir /S /Q \"{backup}\"");
        lines.Add($"rmdir /S /Q \"{fresh}\"");
        if (cleanupDir is not null)
        {
            lines.Add($"rmdir /S /Q \"{Cmd(cleanupDir)}\"");
        }

        lines.Add("goto start");

        // Rollback: added files out, old files back; the backup is deleted only when every old file is back.
        lines.Add(":rollback");
        foreach (var file in plan.Added)
        {
            lines.Add($"if exist \"{app}\\{Win(file.Path)}\" del /F /Q \"{app}\\{Win(file.Path)}\"");
        }

        foreach (var file in plan.Replaced.Concat(plan.Removed))
        {
            Retry($"move /Y \"{backup}\\{Win(file.Path)}\" \"{app}\\{Win(file.Path)}\"", "set \"KEEP_BACKUP=1\"",
                skipUnless: $"{backup}\\{Win(file.Path)}");
        }

        lines.Add("if defined KEEP_BACKUP goto broken");
        lines.Add($"rmdir /S /Q \"{backup}\"");
        lines.Add(":abort");
        lines.Add($"if exist \"{fresh}\" rmdir /S /Q \"{fresh}\"");
        lines.Add($">\"{Cmd(failedMark)}\" echo delta failed");
        lines.Add(":start");
        if (relaunchExe is not null)
        {
            lines.Add($"start \"\" \"{Cmd(relaunchExe)}\"");
        }

        lines.Add("del \"%~f0\" & exit /b");

        // An old file could not be put back: the folder mixes two versions. Keep the backup, do not start the app.
        lines.Add(":broken");
        lines.Add($">\"{Cmd(failedMark)}\" echo delta failed, backup kept");
        lines.Add("del \"%~f0\" & exit /b");
        return string.Join("\r\n", lines) + "\r\n";

        // One command with retries; onFail runs after the last failed try. skipUnless: nothing to do when it is missing.
        void Retry(string command, string onFail, string? skipUnless = null)
        {
            var label = "s" + (++step).ToString(System.Globalization.CultureInfo.InvariantCulture);
            lines.Add("set \"TRY=0\"");
            if (skipUnless is not null)
            {
                lines.Add($"if not exist \"{skipUnless}\" goto {label}ok");
            }

            lines.Add($":{label}");
            lines.Add($"{command} >nul 2>nul && goto {label}ok");
            lines.Add("set /a TRY+=1");
            lines.Add($"if %TRY% lss {tries} ( ping -n 2 127.0.0.1 >nul & goto {label} )");
            lines.Add(onFail);
            lines.Add($":{label}ok");
        }

        static IEnumerable<string> Folders(IEnumerable<DeltaFile> files)
        {
            var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var parts = file.Path.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    folders.Add(string.Join('\\', parts[..i]));
                }
            }

            return folders;
        }
    }
}
