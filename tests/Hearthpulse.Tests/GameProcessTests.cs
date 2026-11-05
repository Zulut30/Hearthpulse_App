using System.Diagnostics;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Tests;

/// <summary>Points the process lookup at this test process, so it runs alone.</summary>
[CollectionDefinition(nameof(GlobalGameProcess), DisableParallelization = true)]
public sealed class GlobalGameProcess;

/// <summary>
/// With the game closed the lookup must not snapshot every process on each call (11 ms of kernel time, more than once
/// a second all day): it waits for a Unity window or the next gap.
/// </summary>
[Collection(nameof(GlobalGameProcess))]
public sealed class GameProcessTests
{
    [Fact]
    public void A_closed_game_is_looked_for_again_when_a_window_may_be_its_own_not_on_every_call()
    {
        var names = GameProcess.Names;
        var probe = GameProcess.MayBeRunning;
        try
        {
            GameProcess.Forget();
            var window = false;
            GameProcess.MayBeRunning = () => window;
            GameProcess.Names = ["Hearthpulse-no-such-process"];
            Assert.False(GameProcess.IsRunning());

            // The "game" starts (this process stands in for it): no snapshot until a Unity window may be there.
            GameProcess.Names = [Process.GetCurrentProcess().ProcessName];
            Assert.False(GameProcess.IsRunning());
            window = true;
            Assert.True(GameProcess.IsRunning());

            // Held: no snapshot needed while it runs, whatever the windows.
            window = false;
            Assert.True(GameProcess.IsRunning());
        }
        finally
        {
            GameProcess.Names = names;
            GameProcess.MayBeRunning = probe;
            GameProcess.Forget();
        }
    }

    /// <summary>
    /// T3: the held game is the one asked about until it exits; then it is looked for again at once, not after the gap.
    /// A copy of ping (sleep elsewhere) under a name of its own stands in for the game, so no other process matches.
    /// </summary>
    [Fact]
    public void The_held_game_answers_while_it_runs_and_its_exit_is_seen_at_once()
    {
        var names = GameProcess.Names;
        var probe = GameProcess.MayBeRunning;
        using var game = StartStandIn();
        try
        {
            GameProcess.Forget();
            GameProcess.MayBeRunning = () => true;
            GameProcess.Names = [game.ProcessName];
            Listed(game);

            Assert.True(GameProcess.IsRunning());
            Assert.Equal(game.StartTime, GameProcess.StartTime());
            // A console process has no main window, and no other process of that name has one.
            Assert.Equal(IntPtr.Zero, GameProcess.MainWindow());

            // From here no Unity window "appears": only the held process's own exit can make the lookup look again.
            GameProcess.MayBeRunning = () => false;
            game.Kill();
            game.WaitForExit();
            Assert.False(GameProcess.IsRunning());
            Assert.Null(GameProcess.StartTime());
            Assert.Equal(IntPtr.Zero, GameProcess.MainWindow());

            // The game starts again: its window is up, and the new process is found on the first look.
            using var again = StartStandIn(game.StartInfo.FileName);
            Listed(again);
            GameProcess.MayBeRunning = () => true;
            Assert.True(GameProcess.IsRunning());
            Assert.Equal(again.StartTime, GameProcess.StartTime());
            again.Kill();
            again.WaitForExit();
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
            }

            GameProcess.Names = names;
            GameProcess.MayBeRunning = probe;
            GameProcess.Forget();
        }
    }

    /// <summary>
    /// Waits until the system lists the stand-in under its name, so a failure below is the lookup's, not the stand-in's.
    /// The bound only ends a hang.
    /// </summary>
    private static void Listed(Process process)
    {
        var name = Path.GetFileNameWithoutExtension(process.StartInfo.FileName);
        bool Found()
        {
            var all = Process.GetProcessesByName(name);
            var found = all.Any(p => p.Id == process.Id);
            foreach (var p in all)
            {
                p.Dispose();
            }

            return found;
        }

        Assert.True(SpinWait.SpinUntil(Found, TimeSpan.FromSeconds(10)), $"{name} ({process.Id}, exited: {process.HasExited}) is not listed by its name");
    }

    /// <summary>
    /// A sleeping process under a name of its own: a copy of ping (sleep on macOS), or on Linux a shell script, as a
    /// copied sleep there may be a multi-call binary that quits under a name it does not know. <paramref name="copy"/>
    /// starts the same file again.
    /// </summary>
    private static Process StartStandIn(string? copy = null)
    {
        var windows = OperatingSystem.IsWindows();
        if (copy is null)
        {
            // A short name: Linux keeps 15 characters of a process's name.
            copy = Path.Combine(TestDataFolder.Scratch("hpgame"), "hpg" + Guid.NewGuid().ToString("N")[..8] + (windows ? ".exe" : string.Empty));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            if (OperatingSystem.IsLinux())
            {
                // The script's name is the process's; a short sleep in a loop goes soon after the script is killed.
                File.WriteAllText(copy, "#!/bin/sh\nwhile :; do sleep 1; done\n");
                File.SetUnixFileMode(copy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                File.Copy(windows ? Path.Combine(Environment.SystemDirectory, "PING.EXE") : "/bin/sleep", copy);
            }
        }

        var info = new ProcessStartInfo(copy, windows ? "-n 120 127.0.0.1" : OperatingSystem.IsLinux() ? string.Empty : "120")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        return Process.Start(info)!;
    }
}
