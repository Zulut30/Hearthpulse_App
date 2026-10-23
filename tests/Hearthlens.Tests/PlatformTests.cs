using Hearthlens.Core.Hearthstone;

namespace Hearthlens.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hl-platform-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Hearthstone\Hearthstone.exe", "/p/drive_c/Program Files (x86)/Hearthstone/Hearthstone.exe")]
    [InlineData(@"D:\Games\Hearthstone\Hearthstone.exe", "/p/dosdevices/d:/Games/Hearthstone/Hearthstone.exe")]
    [InlineData(@"c:\", "/p/drive_c")]
    [InlineData("Hearthstone.exe", null)]
    public void Wine_paths_map_into_the_prefix(string windows, string? expected)
    {
        Assert.Equal(expected, HearthstoneInstall.MapWinePath(windows, "/p"));
    }

    [Fact]
    public void Wine_log_config_prefers_an_existing_file_then_the_linux_user()
    {
        var install = Path.Combine(_root, "drive_c", "Program Files (x86)", "Hearthstone");
        Directory.CreateDirectory(install);
        foreach (var user in new[] { "Public", "steamuser", "alice" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "drive_c", "users", user));
        }

        var config = HearthstoneInstall.WineLogConfigPath(install, "alice");
        Assert.Equal(Path.Combine(_root, "drive_c", "users", "alice", "AppData", "Local", "Blizzard", "Hearthstone", "log.config"), config);

        var existing = Path.Combine(_root, "drive_c", "users", "steamuser", "AppData", "Local", "Blizzard", "Hearthstone", "log.config");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "[Power]\n");
        Assert.Equal(existing, HearthstoneInstall.WineLogConfigPath(install, "alice"));
    }

    [Fact]
    public void Wine_log_config_is_unknown_outside_a_prefix()
    {
        Assert.Null(HearthstoneInstall.WineLogConfigPath(_root, "alice"));
    }

    [Fact]
    public void Installs_are_recognised_on_windows_macos_and_android()
    {
        var windows = Directory.CreateDirectory(Path.Combine(_root, "win")).FullName;
        File.WriteAllText(Path.Combine(windows, "Hearthstone.exe"), string.Empty);
        var mac = Directory.CreateDirectory(Path.Combine(_root, "mac", "Hearthstone.app")).Parent!.FullName;
        var android = Directory.CreateDirectory(Path.Combine(_root, "Android", "data", HearthstoneInstall.AndroidPackage, "files")).FullName;

        Assert.True(HearthstoneInstall.IsInstall(windows));
        Assert.True(HearthstoneInstall.IsInstall(mac));
        Assert.True(HearthstoneInstall.IsInstall(android));
        Assert.False(HearthstoneInstall.IsInstall(_root));
        Assert.False(HearthstoneInstall.IsInstall(null));
    }

    [Fact]
    public void Session_folders_are_found_in_the_install()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "Logs")).FullName;
        Directory.CreateDirectory(Path.Combine(logs, "Hearthstone_2026_10_20_18_00_00"));
        Directory.CreateDirectory(Path.Combine(logs, "Hearthstone_2026_10_21_09_30_15"));

        var latest = HearthstoneInstall.LatestLogSession(_root);
        Assert.Equal("Hearthstone_2026_10_21_09_30_15", Path.GetFileName(latest));
        Assert.Equal(new DateTime(2026, 10, 21, 9, 30, 15), HearthstoneInstall.SessionStart(latest!));
    }
}
