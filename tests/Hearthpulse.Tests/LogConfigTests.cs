using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Tests;

/// <summary>
/// T3: the client's log.config, on a file of the test's own (the real one lives in the user's AppData and is never
/// touched here): which sections the tracker needs, and a fix that keeps everything else. Also the session folders.
/// </summary>
public sealed class LogConfigTests : IDisposable
{
    private readonly string _dir = TestDataFolder.Scratch("log-config");

    private string ConfigPath => Path.Combine(_dir, "Blizzard", "Hearthstone", "log.config");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_misses_every_section_and_the_fix_writes_them_all_once()
    {
        Assert.Equal(["Power", "LoadingScreen", "Decks", "Arena", "FullScreenFX", "Achievements"], LogConfig.MissingSectionsIn(ConfigPath));

        Assert.True(LogConfig.EnsureIn(ConfigPath));
        Assert.Empty(LogConfig.MissingSectionsIn(ConfigPath));
        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("[Power]", text, StringComparison.Ordinal);
        Assert.Contains("Verbose=True", text, StringComparison.Ordinal);
        Assert.False(LogConfig.EnsureIn(ConfigPath)); // nothing left to change: the file is not rewritten
    }

    [Fact]
    public void Sections_of_other_trackers_stay_and_a_quiet_power_log_is_made_verbose()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath,
        [
            "[Power]", "LogLevel=1", "FilePrinting=true", "Verbose=false",
            "[Decks]", "LogLevel=1", "FilePrinting=True",
            "[Zone]", "LogLevel=1", "FilePrinting=True", "ConsolePrinting=True",
            "[Arena]", "LogLevel=0", "FilePrinting=True",
        ]);

        // Power wants Verbose; Arena's level is off; the rest are absent. Decks is fine (case does not matter).
        Assert.Equal(["Power", "LoadingScreen", "Arena", "FullScreenFX", "Achievements"], LogConfig.MissingSectionsIn(ConfigPath));
        Assert.True(LogConfig.EnsureIn(ConfigPath));

        Assert.Empty(LogConfig.MissingSectionsIn(ConfigPath));
        var lines = File.ReadAllLines(ConfigPath);
        var zone = Array.IndexOf(lines, "[Zone]");
        Assert.True(zone >= 0, "another tracker's section is kept");
        Assert.Equal("ConsolePrinting=True", lines[zone + 3]);
    }

    [Fact]
    public void Session_folders_list_oldest_first_and_the_latest_is_the_newest_name()
    {
        var logs = Path.Combine(_dir, "Logs");
        foreach (var name in new[] { "Hearthstone_2026_11_02_10_00_00", "Hearthstone_2026_10_30_21_15_09", "Hearthstone_2026_11_04_08_30_00", "Other" })
        {
            Directory.CreateDirectory(Path.Combine(logs, name));
        }

        Assert.Equal(["Hearthstone_2026_10_30_21_15_09", "Hearthstone_2026_11_02_10_00_00", "Hearthstone_2026_11_04_08_30_00"],
            HearthstoneInstall.AllLogSessions(_dir).Select(Path.GetFileName));
        Assert.Equal("Hearthstone_2026_11_04_08_30_00", Path.GetFileName(HearthstoneInstall.LatestLogSession(_dir)));
        Assert.Equal(new DateTime(2026, 10, 30, 21, 15, 9), HearthstoneInstall.SessionStart(Path.Combine(logs, "Hearthstone_2026_10_30_21_15_09")));
        Assert.Null(HearthstoneInstall.SessionStart(Path.Combine(logs, "Other")));

        var empty = TestDataFolder.Scratch("no-logs");
        Assert.Empty(HearthstoneInstall.AllLogSessions(empty));
        Assert.Null(HearthstoneInstall.LatestLogSession(empty));
    }

    [Fact]
    public void Client_config_lives_in_the_install_folder_and_an_unlimited_log_counts_only_once_the_client_read_it()
    {
        Assert.Null(ClientConfig.PathFor(null));
        Assert.Null(ClientConfig.PathFor(string.Empty));
        Assert.Equal(Path.Combine(_dir, "client.config"), ClientConfig.PathFor(_dir));

        // No install: nothing to limit. The 10 MB default: the limit applies.
        Assert.False(ClientConfig.LimitActive(null));
        Directory.CreateDirectory(_dir);
        Assert.True(ClientConfig.LimitActive(_dir));
    }
}
