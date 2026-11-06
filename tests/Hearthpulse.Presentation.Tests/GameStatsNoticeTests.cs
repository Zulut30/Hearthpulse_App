using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.GameStats;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Game records (docs/game-stats.md): on by default, nothing sent before the one-time notice in the main window, whose
/// buttons keep them, turn them off or open the policy, in both apps' view models.
/// </summary>
public sealed class GameStatsNoticeTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static dynamic Model(bool wpf, AppSettings settings) =>
        wpf ? new WpfMain(History(), settings, TestApp.Data) : new AvaMain(History(), settings, TestApp.Data);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_notice_shows_once_with_a_pool_and_ok_starts_sending(bool wpf)
    {
        var settings = new AppSettings { PoolEndpoint = "https://pool.test", PoolConsent = false };
        var model = Model(wpf, settings);
        var changes = new List<bool>();
        model.GameStatsChanged += new Action<bool>(changes.Add);

        Assert.True((bool)model.ShowGameStatsNotice);
        Assert.True((bool)model.GameStatsSharing);
        Assert.False(GameStatsClient.Sharing(settings));

        model.AnswerGameStats.Execute("ok");

        Assert.False((bool)model.ShowGameStatsNotice);
        Assert.True(GameStatsClient.Sharing(settings));
        Assert.Equal(64, settings.PoolSecret?.Length);
        Assert.Equal([true], changes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Off_on_the_notice_turns_records_off(bool wpf)
    {
        var settings = new AppSettings { PoolEndpoint = "https://pool.test", PoolConsent = false };
        var model = Model(wpf, settings);
        model.AnswerGameStats.Execute("off");

        Assert.True(settings.GameStatsOff);
        Assert.False((bool)model.GameStatsSharing);
        Assert.False((bool)model.ShowGameStatsNotice);
        Assert.False(GameStatsClient.Sharing(settings));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_a_pool_there_is_no_notice(bool wpf) =>
        Assert.False((bool)Model(wpf, new AppSettings()).ShowGameStatsNotice);
}
