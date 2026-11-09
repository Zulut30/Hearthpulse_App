using System.Diagnostics;
using System.Security.Cryptography;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The setup helper must not fail silently: it waits at most two minutes for the app, backs the app folder up, runs the
/// setup and once more on failure, puts the folder back after a second failure, and leaves a result file for the next
/// start. The text is checked everywhere; on Windows the script really runs, in temp folders whose names carry spaces,
/// %, !, &amp; and Cyrillic, with a fake setup (a .cmd that changes the folder and exits 0 or 2). Never the real setup.
/// </summary>
public sealed class UpdateInstallScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hp-install {Guid.NewGuid():N} 100% ! & Привет");
    private readonly string _scripts = Path.Combine(Path.GetTempPath(), $"hp-install-scripts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _scripts })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_setup_helper_waits_backs_up_retries_restores_and_reports()
    {
        var script = UpdateService.BuildApplyScript(1234, @"C:\data\updates\setup.exe", @"C:\data\updates\setup.exe", @"C:\Apps\Hearthpulse\",
            @"C:\Apps\Hearthpulse\Hearthpulse.exe", installer: true, cleanupDir: null, resultFile: @"C:\data\update-state\install-result.txt",
            expectedSha256: new string('a', 64));

        Assert.Contains("PID eq 1234", script);
        Assert.Contains("if %waited% GEQ 120 goto closed", script);
        Assert.Contains("\"%SystemRoot%\\System32\\robocopy.exe\" \"C:\\Apps\\Hearthpulse\" \"C:\\Apps\\Hearthpulse.setup-backup\" /MIR", script);
        Assert.Equal(2, Count(script, "start \"\" /b /wait \"C:\\data\\updates\\setup.exe\" /S"));
        Assert.Contains("\"%SystemRoot%\\System32\\PING.EXE\" -n 6 127.0.0.1 >nul", script);
        Assert.Contains("\"%SystemRoot%\\System32\\robocopy.exe\" \"C:\\Apps\\Hearthpulse.setup-backup\" \"C:\\Apps\\Hearthpulse\" /MIR", script);
        // System tools by full path: a Git Bash find.exe earlier in PATH must not decide the wait.
        Assert.Contains("\"%SystemRoot%\\System32\\tasklist.exe\" /FI \"PID eq 1234\" /NH 2>nul | \"%SystemRoot%\\System32\\find.exe\" \"1234\" >nul", script);
        Assert.DoesNotContain("| find ", script);
        // The setup's SHA-256 is checked again right before it runs.
        Assert.Contains("\"%SystemRoot%\\System32\\certutil.exe\" -hashfile \"C:\\data\\updates\\setup.exe\" SHA256 2>nul | \"%SystemRoot%\\System32\\find.exe\" /i \"" + new string('a', 64) + "\" >nul", script);
        Assert.Contains("set code=hash_mismatch", script);
        Assert.Contains(") > \"C:\\data\\update-state\\install-result.txt\"", script);
        foreach (var line in new[] { "echo exit=%code%", "echo attempts=%attempts%", "echo backup=%backup%", "echo restored=%restored%" })
        {
            Assert.Contains(line, script);
        }

        Assert.Contains("start \"\" \"C:\\Apps\\Hearthpulse\\Hearthpulse.exe\"", script);
        Assert.DoesNotContain("EnableDelayedExpansion", script);
        Assert.DoesNotContain(".update-old", script);
        Assert.EndsWith("del \"%~f0\"\r\n", script);
    }

    [Fact]
    public void Paths_with_percent_signs_are_doubled_everywhere()
    {
        var script = UpdateService.BuildApplyScript(1, @"C:\Users\A&B !x\100%\setup.exe", @"C:\s", @"C:\Users\A&B !x\100%\app", null, installer: true, cleanupDir: null,
            resultFile: @"C:\Users\A&B !x\100%\state\install-result.txt");
        Assert.DoesNotContain(@"100%\", script);
        Assert.Contains(@"""C:\Users\A&B !x\100%%\app.setup-backup""", script);
        Assert.Contains(@"""C:\Users\A&B !x\100%%\state\install-result.txt""", script);
    }

    [Fact]
    public void A_setup_that_succeeds_installs_once_and_removes_the_backup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (app, result, setup) = Prepare(exitCode: 0);
        Run(UpdateService.BuildApplyScript(ExitedPid(), setup, setup, app, null, installer: true, cleanupDir: null, result, Sha(setup), waitSeconds: 120, retrySeconds: 1));

        Assert.Equal(["exit=0", "attempts=1", "backup=1", "restored=0"], File.ReadAllLines(result));
        Assert.Equal("new", File.ReadAllText(Path.Combine(app, "Hearthpulse.dll")).Trim());
        Assert.True(File.Exists(Path.Combine(app, "added.dll")));
        Assert.False(Directory.Exists(app + ".setup-backup"));
        Assert.Single(File.ReadAllLines(Path.Combine(_root, "runs.txt")));
    }

    [Fact]
    public void A_setup_that_fails_twice_is_rolled_back_to_the_exact_old_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (app, result, setup) = Prepare(exitCode: 2);
        var before = Hashes(app);
        Run(UpdateService.BuildApplyScript(ExitedPid(), setup, setup, app, null, installer: true, cleanupDir: null, result, Sha(setup), waitSeconds: 120, retrySeconds: 1));

        Assert.Equal(["exit=2", "attempts=2", "backup=1", "restored=1"], File.ReadAllLines(result));
        Assert.Equal(before, Hashes(app));
        Assert.False(Directory.Exists(app + ".setup-backup"));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_root, "runs.txt")).Length);
    }

    [Fact]
    public void A_hung_app_does_not_hold_the_update_past_the_wait()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // This test process stays alive: the helper must give up waiting and install anyway.
        var (app, result, setup) = Prepare(exitCode: 0);
        var watch = Stopwatch.StartNew();
        Run(UpdateService.BuildApplyScript(Environment.ProcessId, setup, setup, app, null, installer: true, cleanupDir: null, result, Sha(setup), waitSeconds: 2, retrySeconds: 1));
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 50);
        Assert.Equal("exit=0", File.ReadAllLines(result)[0]);
    }

    [Fact]
    public void A_setup_changed_after_its_check_is_not_run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (app, result, setup) = Prepare(exitCode: 0);
        var before = Hashes(app);
        Run(UpdateService.BuildApplyScript(ExitedPid(), setup, setup, app, null, installer: true, cleanupDir: null, result, new string('0', 64), waitSeconds: 120, retrySeconds: 1));

        Assert.Equal(["exit=hash_mismatch", "attempts=0", "backup=0", "restored=0"], File.ReadAllLines(result));
        Assert.Equal(before, Hashes(app));
        Assert.False(File.Exists(Path.Combine(_root, "runs.txt")));
        Assert.False(Directory.Exists(app + ".setup-backup"));
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private (string App, string Result, string Setup) Prepare(int exitCode)
    {
        var app = Path.Combine(_root, "Hearthpulse");
        Directory.CreateDirectory(Path.Combine(app, "runtimes", "win-x64"));
        File.WriteAllText(Path.Combine(app, "Hearthpulse.dll"), "old");
        File.WriteAllText(Path.Combine(app, "Hearthpulse.exe"), "old exe");
        File.WriteAllText(Path.Combine(app, "runtimes", "win-x64", "native.dll"), "native");
        var result = Path.Combine(_root, "data", "update-state", "install-result.txt");

        // The fake setup changes the folder the way a half-done install would, notes the run and exits with the code.
        // "exit" without /b: start runs a .cmd under cmd /K, and only a plain exit ends it with this code. It sits in a
        // plain folder: cmd /K drops the quotes around a path with "&" or "(" (a real setup .exe is started directly).
        static string Q(string path) => path.Replace("%", "%%", StringComparison.Ordinal);
        Directory.CreateDirectory(_scripts);
        var setup = Path.Combine(_scripts, "Hearthpulse-Setup-9.9.9-test.cmd");
        File.WriteAllText(setup, string.Join("\r\n",
            "@echo off",
            "chcp 65001 >nul",
            $"echo new> \"{Q(Path.Combine(app, "Hearthpulse.dll"))}\"",
            $"echo added> \"{Q(Path.Combine(app, "added.dll"))}\"",
            $"del \"{Q(Path.Combine(app, "runtimes", "win-x64", "native.dll"))}\"",
            $"echo run>> \"{Q(Path.Combine(_root, "runs.txt"))}\"",
            $"exit {exitCode}") + "\r\n");

        return (app, result, setup);
    }

    private void Run(string script)
    {
        Directory.CreateDirectory(_scripts);
        var path = Path.Combine(_scripts, "apply.cmd");
        File.WriteAllText(path, script);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{path}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            WorkingDirectory = _scripts,
        })!;
        process.StandardInput.Close();
        Assert.True(process.WaitForExit(90_000), "the helper did not finish");
    }

    private static Dictionary<string, string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    private static int ExitedPid()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
