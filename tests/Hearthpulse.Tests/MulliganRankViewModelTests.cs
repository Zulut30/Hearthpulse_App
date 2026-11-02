using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The meta page's mulligan rank filter (plan 2.4): its chips, the saved setting and the rows of a slice.</summary>
public sealed class MulliganRankViewModelTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-mulligan-rank-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _history;

    public MulliganRankViewModelTests() => _history = new HistoryStore(_db);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public void Legend_is_the_default_and_a_picked_rank_is_saved_and_announced()
    {
        var settings = new AppSettings();
        var vm = new MainViewModel(_history, settings, TestDataFolder.App);
        var announced = 0;
        vm.MulliganRankChanged += () => announced++;

        Assert.Equal(["all", "diamond", "legend", "top_legend"], vm.MulliganRanks.Select(c => c.Key));
        Assert.Equal("legend", Assert.Single(vm.MulliganRanks, c => c.Selected).Key);

        vm.SetMulliganRank.Execute("top_legend");
        vm.SetMulliganRank.Execute("top_legend");
        vm.SetMulliganRank.Execute("bronze");

        Assert.Equal("top_legend", settings.MulliganRank);
        Assert.Equal(1, announced);
        Assert.Equal("top_legend", Assert.Single(vm.MulliganRanks, c => c.Selected).Key);
    }

    [Fact]
    public void A_slice_lists_cards_kept_often_enough_best_first()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        var slice = new MulliganSlice("diamond_4to1", MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, 5000,
        [
            new SliceCard("A", 1, 900, 0.01, 600, 0.02, 300, 0.01),
            new SliceCard("B", 2, 900, 0.03, 700, 0.05, 300, 0.02),
            new SliceCard("C", 3, 900, 0.10, 50, 0.20, 300, 0.10), // kept too rarely to count
        ]);

        var rows = vm.MulliganRowsOf(slice, 0.5);

        Assert.Equal(["B", "A"], rows.Select(r => r.CardId));
        Assert.Equal(0.05, rows[0].Edge, 6);
    }

    [Fact]
    public void The_page_shows_the_base_win_rate_of_the_slice_and_says_when_it_fell_back_or_has_none()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        var slice = new MulliganSlice("all", MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, 5000, [new SliceCard("B", 2, 900, 0.03, 700, 0.05, 300, 0.02)]);

        // The base the overlay uses too (RankSlice.Baseline), not the meta page's own win rate.
        vm.ShowRankSlice(new RankSlice(slice, 0.47, AllRanks: true), "top_legend");
        Assert.Equal((0.52).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), Assert.Single(vm.Mulligan).WinRate);
        Assert.Equal(Hearthpulse.Core.Localization.Loc.F("Main.Meta.MulliganRankFallback", Hearthpulse.Core.Localization.Loc.T("Core.MulliganRankSource.top_legend")), vm.MulliganSource);

        // Offline or a 404 for both slices.
        vm.ShowRankSlice(null, "diamond");
        Assert.Empty(vm.Mulligan);
        Assert.Equal(Hearthpulse.Core.Localization.Loc.F("Main.Meta.MulliganRankNone", Hearthpulse.Core.Localization.Loc.T("Core.MulliganRankSource.diamond")), vm.MulliganSource);
    }
}
