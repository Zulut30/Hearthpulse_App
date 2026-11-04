using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Tavern Brawl page through the shared view model (<c>docs/brawl.md</c>, AC2/AC3): community rows from the
/// loaded numbers, the player's classes in the Brawl played last, and every empty state.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class BrawlPageTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTime Monday = new(2026, 10, 26, 18, 0, 0, DateTimeKind.Local);

    private static GameRecord Game(string key, int? scenario, DateTime started, string playerClass, string result = "Win", string mode = "Brawl") => new()
    {
        Key = key,
        StartedAt = started,
        EndedAt = started.AddMinutes(8),
        Mode = mode,
        GameType = mode == "Brawl" ? "GT_TAVERNBRAWL" : "GT_RANKED",
        PlayerClass = playerClass,
        OpponentClass = "MAGE",
        Result = result,
        Turns = 7,
        ScenarioId = scenario,
    };

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "the Brawl page was not rebuilt in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Community_rows_are_the_classes_strongest_first_with_share_unclassed_games_and_source()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        Assert.True(vm.BrawlClassesLoading);
        Assert.False(vm.BrawlClassesUnavailable);

        var meta = new BrawlMeta(
            [
                new BrawlClassStat("PRIEST", 0.561, 0.124, 3540),
                new BrawlClassStat("MAGE", 0.48, 0.30, 8000),
                new BrawlClassStat("WARRIOR", 0.52, 0.0001, 3),
                new BrawlClassStat("ROGUE", 0.50, 0.10, 2900),
            ],
            28515,
            1240);
        vm.SetBrawlMeta(meta);
        await Until(() => vm.BrawlClasses.Count == 4);

        var culture = CultureInfo.CurrentCulture;
        // Strongest first by default.
        Assert.Equal(new[] { "PRIEST", "WARRIOR", "ROGUE", "MAGE" }.Select(MainViewModel.ClassHero), vm.BrawlClasses.Select(r => r.CardId));
        var priest = vm.BrawlClasses[0];
        Assert.Equal(0.561.ToString("P1", culture), priest.WinRate);
        Assert.Equal(string.Empty, priest.Record);
        Assert.Equal(3540, priest.GameCount);
        Assert.Equal(Loc.F("Brawl.Classes.Sub", Loc.P("Stats.Games", 3540), 0.124.ToString("P1", culture)), priest.Games);
        Assert.False(priest.Vs.Shown);
        // Best and worst only among classes with enough games: Warrior's 3 games are not marked.
        Assert.True(priest.WinBest);
        Assert.True(vm.BrawlClasses[3].WinWorst);
        Assert.Equal(0, vm.BrawlClasses[1].WinMark);
        Assert.Equal(0, vm.BrawlClasses[2].WinMark);

        Assert.Equal(Loc.F("Brawl.Classes.Unclassed", Loc.P("Stats.Games", 1240)), vm.BrawlUnclassed);
        Assert.Equal("Hearthpulse data · " + Loc.P("Stats.Games", 28515), vm.BrawlSource);
        Assert.False(vm.BrawlClassesLoading);
        Assert.False(vm.BrawlClassesNone);

        // A failed refresh (or nothing from the server and the cache) keeps what is shown.
        vm.BrawlMetaUnavailable();
        vm.SetBrawlMeta(BrawlMeta.Empty);
        Assert.Equal(4, vm.BrawlClasses.Count);
        Assert.False(vm.BrawlClassesUnavailable);
    }

    [Fact]
    public async Task Numbers_over_a_week_old_say_when_they_were_fetched()
    {
        // Review m1: the server has not answered for a while; "this week's" would be wrong.
        Loc.Instance.SetLanguage(Loc.English);
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        var fresh = new BrawlMeta([new BrawlClassStat("PRIEST", 0.56, 0.12, 3540)], 3540, 0) { FetchedAt = DateTime.UtcNow.AddDays(-1) };
        vm.SetBrawlMeta(fresh);
        await Until(() => vm.BrawlClasses.Count == 1);
        Assert.Equal(Loc.T("Brawl.Classes.Note"), vm.BrawlClassesNote);

        var fetched = DateTime.UtcNow.AddDays(-8);
        vm.SetBrawlMeta(fresh with { FetchedAt = fetched });
        await Until(() => vm.BrawlClassesNote != Loc.T("Brawl.Classes.Note"));
        Assert.Equal(Loc.F("Brawl.Classes.NoteStale", fetched.ToLocalTime()), vm.BrawlClassesNote);
    }

    [Fact]
    public async Task Community_empty_states_loading_unavailable_and_no_numbers()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var offline = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        offline.SetBrawlMeta(BrawlMeta.Empty);
        Assert.True(offline.BrawlClassesUnavailable);
        Assert.False(offline.BrawlClassesLoading);
        Assert.False(offline.BrawlClassesNone);

        // An answer without archetypes: no rows, no source.
        var none = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        none.SetBrawlMeta(new BrawlMeta([], 0, 0));
        await Until(() => none.BrawlClassesNone);
        Assert.Empty(none.BrawlClasses);
        Assert.Equal(string.Empty, none.BrawlSource);
        Assert.Equal(string.Empty, none.BrawlUnclassed);

        // Archetypes, but none with a class: the unclassed line and the source stay.
        var unclassed = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        unclassed.SetBrawlMeta(new BrawlMeta([], 300, 300));
        await Until(() => unclassed.BrawlClassesNone);
        Assert.Equal(Loc.F("Brawl.Classes.Unclassed", Loc.P("Stats.Games", 300)), unclassed.BrawlUnclassed);
        Assert.Equal("Hearthpulse data · " + Loc.P("Stats.Games", 300), unclassed.BrawlSource);
        Assert.False(unclassed.BrawlClassesUnavailable);
    }

    [Fact]
    public async Task Own_rows_are_the_classes_of_the_brawl_played_last()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var store = HistoryStore.InMemory();
        Assert.True(store.Add(Game("old", 3001, Monday, "PRIEST")));
        Assert.True(store.Add(Game("before-column", null, Monday.AddHours(1), "WARLOCK")));
        Assert.True(store.Add(Game("ranked", null, Monday.AddDays(7), "MAGE", mode: "Ranked")));
        Assert.True(store.Add(Game("w1", 3002, Monday.AddDays(7).AddHours(1), "MAGE")));
        Assert.True(store.Add(Game("w2", 3002, Monday.AddDays(7).AddHours(2), "MAGE", "Loss")));
        Assert.True(store.Add(Game("w3", 3002, Monday.AddDays(7).AddHours(3), "MAGE")));
        Assert.True(store.Add(Game("w4", 3002, Monday.AddDays(7).AddHours(4), "WARRIOR", "Loss")));
        Assert.True(store.Add(Game("gone", 3002, Monday.AddDays(7).AddHours(5), "ROGUE", GameRecord.AbandonedResult)));

        var vm = new MainViewModel(store, new AppSettings(), TestDataFolder.App);
        Assert.True(vm.BrawlMineEmpty);
        await vm.RefreshAsync();
        await Until(() => vm.BrawlMine.Count == 2);

        var mage = vm.BrawlMine[0];
        Assert.Equal(MainViewModel.ClassHero("MAGE"), mage.CardId);
        Assert.Equal("2–1", mage.Record);
        Assert.Equal(Loc.P("Stats.Games", 3), mage.Games);
        Assert.Equal((2 / 3.0).ToString("P0", CultureInfo.CurrentCulture), mage.WinRate);
        Assert.Equal("0–1", vm.BrawlMine[1].Record);
        Assert.Equal(Loc.F("Brawl.Mine.Note", Monday.AddDays(7).AddHours(1)), vm.BrawlMineNote);
        Assert.False(vm.BrawlMineEmpty);
        Assert.False(vm.BrawlMineOld);

        // A new game of a newer Brawl replaces the table once History has it.
        Assert.True(store.Add(Game("next", 3003, Monday.AddDays(14), "DRUID")));
        await vm.RefreshAsync();
        await Until(() => vm.BrawlMine.Count == 1 && vm.BrawlMine[0].CardId == MainViewModel.ClassHero("DRUID"));
    }

    [Fact]
    public async Task Own_table_is_empty_without_brawl_games_and_says_why_for_games_before_the_scenario_column()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var store = HistoryStore.InMemory();
        Assert.True(store.Add(Game("ranked", 4, Monday, "MAGE", mode: "Ranked")));
        var vm = new MainViewModel(store, new AppSettings(), TestDataFolder.App);
        await vm.RefreshAsync();
        vm.SetBrawlMeta(new BrawlMeta([], 0, 0));
        await Until(() => vm.BrawlClassesNone);
        Assert.True(vm.BrawlMineEmpty);
        Assert.False(vm.BrawlMineOld);
        Assert.Equal(string.Empty, vm.BrawlMineNote);

        Assert.True(store.Add(Game("before-column", null, Monday.AddHours(1), "WARLOCK")));
        await vm.RefreshAsync();
        await Until(() => vm.BrawlMineOld);
        // Review m2: only the "recorded before this version" line, not the "no games yet" state as well.
        Assert.False(vm.BrawlMineEmpty);
        Assert.Equal(string.Empty, vm.BrawlMineNote);
    }
}
