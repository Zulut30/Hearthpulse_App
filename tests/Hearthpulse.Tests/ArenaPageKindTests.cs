using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// The Arena page's tabs (owner, 2026-11-07): runs and numbers per Arena, the community class win rates only on the
/// regular Arena's tab, History's filters, the card scores of the other Arena's slice never used for a deck score.
/// </summary>
public sealed class ArenaPageKindTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-arena-kind-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _history;

    public ArenaPageKindTests()
    {
        _history = new HistoryStore(_db);
        foreach (var game in ArenaRunsScene.Games().Concat(Underground()))
        {
            _history.Add(game);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_db);
        }
        catch (IOException)
        {
            // My Stats may still be reading in the background; the temp file goes with the temp folder.
        }
    }

    private static IEnumerable<GameRecord> Underground()
    {
        var start = new DateTime(2026, 11, 6, 20, 0, 0);
        for (var i = 0; i < 3; i++)
        {
            yield return new GameRecord
            {
                Key = "underground-" + i,
                StartedAt = start.AddMinutes(i * 15),
                EndedAt = start.AddMinutes((i * 15) + 10),
                Mode = "Arena",
                GameType = "GT_UNDERGROUND_ARENA",
                Format = "FT_WILD",
                Result = i == 1 ? "Loss" : "Win",
                PlayerClass = "WARRIOR",
                OpponentClass = "MAGE",
                DeckId = 991,
            };
        }
    }

    [Fact]
    public async Task Each_tab_shows_its_own_arena_and_underground_has_no_community_class_numbers()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        vm.SetArenaMeta(new ArenaMeta([], [new ArenaClassStat("MAGE", 0.55, 0.1, 0.2, 1000)], underground: false));
        await Runs(vm, () => vm.RefreshAsync(), rows => rows.Count == 2 && vm.ArenaCommunity.Count > 0);
        Assert.True(vm.ArenaTabRegular);
        Assert.False(vm.ArenaCommunityNoSplit);
        Assert.NotEmpty(vm.ArenaCommunity);

        await Runs(vm, () => vm.ArenaTabUnderground = true, rows => rows.Count == 1 && vm.ArenaCommunity.Count == 0);
        Assert.Equal(ArenaKind.Underground, vm.ArenaKindShown);
        Assert.Equal("2–1", vm.ArenaRuns.Single().Record);
        Assert.True(vm.ArenaCommunityNoSplit);
        Assert.Empty(vm.ArenaCommunity);
        Assert.False(vm.ArenaCommunityLoading);
        Assert.False(vm.ArenaCommunityUnavailable);

        await Runs(vm, () => vm.ArenaTabRegular = true, rows => rows.Count == 2 && vm.ArenaCommunity.Count > 0);
        Assert.False(vm.ArenaCommunityNoSplit);
    }

    [Fact]
    public void The_card_scores_of_the_other_arena_never_score_a_deck()
    {
        var card = new ArenaCardScore("CS2_029", 0, 60, 0.5, 0.1, 100);
        var classes = new[] { new ArenaClassStat("MAGE", 0.55, 0.1, 0.2, 1000) };
        var regular = new ArenaMeta([card], classes, underground: false);
        var underground = new ArenaMeta([card], classes, underground: true);

        Assert.Same(regular, MainViewModel.ArenaMetaFor(regular, ArenaKind.Arena));
        Assert.False(MainViewModel.ArenaMetaFor(regular, ArenaKind.Underground)!.HasCards);
        Assert.Empty(MainViewModel.ArenaMetaFor(regular, ArenaKind.Underground)!.Classes);
        var undergroundOwn = MainViewModel.ArenaMetaFor(underground, ArenaKind.Underground)!;
        Assert.True(undergroundOwn.HasCards);
        Assert.Empty(undergroundOwn.Classes);
        var regularFromUnderground = MainViewModel.ArenaMetaFor(underground, ArenaKind.Arena)!;
        Assert.False(regularFromUnderground.HasCards);
        Assert.NotEmpty(regularFromUnderground.Classes);
        Assert.Null(MainViewModel.ArenaMetaFor(null, ArenaKind.Arena));
    }

    [Fact]
    public void A_tab_without_its_card_scores_says_whose_scores_there_are()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        var card = new ArenaCardScore("CS2_029", 0, 60, 0.5, 0.1, 100);

        vm.SetArenaMeta(new ArenaMeta([card], [], underground: true));
        Assert.Equal(Core.Localization.Loc.T("Arena.Page.DeckOtherSliceUnderground"), vm.ArenaDeckEmptyText);
        vm.ArenaTabUnderground = true;
        Assert.Equal(Core.Localization.Loc.T("Arena.Page.DeckEmpty"), vm.ArenaDeckEmptyText);

        vm.SetArenaMeta(new ArenaMeta([card], [], underground: false));
        Assert.Equal(Core.Localization.Loc.T("Arena.Page.DeckOtherSliceRegular"), vm.ArenaDeckEmptyText);
    }

    [Fact]
    public void History_filters_tell_the_two_arenas_apart_and_other_holds_neither()
    {
        var regular = ArenaRunsScene.Games().First();
        var underground = Underground().First();
        var casual = regular with { Mode = "Casual", GameType = "GT_CASUAL" };

        Assert.True(MainViewModel.FilterMatch(regular, "Arena"));
        Assert.False(MainViewModel.FilterMatch(underground, "Arena"));
        Assert.True(MainViewModel.FilterMatch(underground, "Underground"));
        Assert.False(MainViewModel.FilterMatch(regular, "Underground"));
        Assert.False(MainViewModel.FilterMatch(regular, "Other"));
        Assert.False(MainViewModel.FilterMatch(underground, "Other"));
        Assert.True(MainViewModel.FilterMatch(casual, "Other"));
        Assert.True(MainViewModel.FilterMatch(underground, "All"));
    }

    /// <summary>Runs <paramref name="act"/> and waits until the run list it makes (built in the background) matches.</summary>
    private static async Task Runs(MainViewModel vm, Func<Task> act, Func<IReadOnlyList<ArenaRunRow>, bool> done)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            // The run list and the community rows are set one after the other: any change may complete the picture.
            if (e.PropertyName is nameof(MainViewModel.ArenaRuns) or nameof(MainViewModel.ArenaCommunity) && done(vm.ArenaRuns))
            {
                ready.TrySetResult();
            }
        }

        vm.PropertyChanged += Changed;
        try
        {
            await act();
            await ready.Task.WaitAsync(Wait);
        }
        finally
        {
            vm.PropertyChanged -= Changed;
        }
    }

    private static Task Runs(MainViewModel vm, Action act, Func<IReadOnlyList<ArenaRunRow>, bool> done) =>
        Runs(vm, () =>
        {
            act();
            return Task.CompletedTask;
        }, done);
}
