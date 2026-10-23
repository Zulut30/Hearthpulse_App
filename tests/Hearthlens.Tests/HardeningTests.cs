using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Updates;
using Xunit;

namespace Hearthlens.Tests;

public class HardeningTests
{
    [Fact]
    public void UpdateScriptSurvivesAccentsAndPercentSigns()
    {
        var script = UpdateService.BuildApplyScript(
            42, @"C:\Users\Niccolò\100%\setup.exe", @"C:\src", @"C:\Users\Niccolò\100%\app", null, installer: true, cleanupDir: null);

        Assert.Contains("chcp 65001", script);
        Assert.Contains(@"C:\Users\Niccolò\100%%\setup.exe", script);
        Assert.DoesNotContain(@"100%\", script);
    }

    [Fact]
    public void LatestReleaseCarriesTheAssetChecksum()
    {
        var hash = new string('a', 64);
        var json = $$"""
            {"tag_name":"v9.9.9","html_url":"https://example.invalid","assets":[
              {"name":"Hearthlens-Setup-9.9.9-win-x64.exe","browser_download_url":"https://example.invalid/setup.exe","digest":"sha256:{{hash}}"}]}
            """;

        var info = UpdateService.ParseLatest(json, UpdateTarget.WindowsInstalled, "x64");

        Assert.NotNull(info);
        Assert.Equal(hash, info!.Sha256);
    }

    [Fact]
    public void ReleaseWithoutChecksumStillParses()
    {
        const string json = """
            {"tag_name":"v9.9.9","assets":[{"name":"Hearthlens-Setup-9.9.9-win-x64.exe","browser_download_url":"https://example.invalid/setup.exe"}]}
            """;

        Assert.Null(UpdateService.ParseLatest(json, UpdateTarget.WindowsInstalled, "x64")!.Sha256);
    }

    [Fact]
    public void CancelledSimulationStopsEarly()
    {
        var ours = new SimSide([SimMinion.Create("X", 3, 3), SimMinion.Create("X", 3, 3)]);
        var theirs = new SimSide([SimMinion.Create("Y", 2, 4), SimMinion.Create("Y", 2, 4)]);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 4000, Cancel = cancel.Token });

        Assert.Equal(100, odds.Runs);
    }
}
