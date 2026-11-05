using Hearthpulse.Core;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Tests;

/// <summary>An isolated run (visual baselines) finds no Hearthstone of the PC's and never sees it running.</summary>
public sealed class IsolationTests
{
    [Fact]
    public void An_isolated_run_finds_only_the_install_the_settings_name()
    {
        var install = TestDataFolder.Scratch("isolated-hs");
        Directory.CreateDirectory(install);
        File.WriteAllBytes(Path.Combine(install, "Hearthstone.exe"), []);
        Isolation.Forced.Value = true;
        try
        {
            Assert.True(Isolation.On);
            Assert.Null(HearthstoneInstall.FindInstallDirectory());
            Assert.Null(HearthstoneInstall.FindInstallDirectory(Path.Combine(install, "missing")));
            Assert.Equal(install, HearthstoneInstall.FindInstallDirectory(install));
            Assert.False(HearthstoneInstall.IsRunning());
        }
        finally
        {
            Isolation.Forced.Value = null;
        }
    }
}
