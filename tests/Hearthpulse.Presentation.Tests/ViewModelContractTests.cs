using System.ComponentModel;
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hearthpulse.Presentation.Tests;

/// <summary>Exercises both compiled versions of the shared source, including the deliberate host differences.</summary>
public sealed class ViewModelContractTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static GameRecord Game(int number, string deck = "First deck", string opponent = "WARRIOR") => new()
    {
        Key = "presentation-game-" + number,
        StartedAt = new DateTime(2026, 10, 23, 10, 0, 0).AddMinutes(number),
        EndedAt = new DateTime(2026, 10, 23, 10, 5, 0).AddMinutes(number),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win",
        PlayerClass = "MAGE", OpponentClass = opponent, DeckName = deck,
    };

    [Fact]
    public async Task History_rows_match_while_each_host_keeps_its_existing_count_and_label_rules()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var history = History();
        // The desktop shows the whole database count; the preview deliberately counts its 400 recent rows.
        for (var i = 0; i < 401; i++) Assert.True(history.Add(Game(i)));
        var wpf = new WpfMain(history, new AppSettings());
        var ava = new AvaMain(history, new AppSettings());
        await wpf.RefreshAsync();
        await ava.RefreshAsync();

        Assert.Equal(401, wpf.HistoryCount);
        Assert.Equal(400, ava.HistoryCount);
        Assert.Equal(wpf.Recent.Select(r => (r.Key, r.Hero, r.Result, r.Detail)),
            ava.Recent.Select(r => (r.Key, r.Hero, r.Result, r.Detail)));
        Assert.Equal(wpf.OverviewTiles.Select(t => t.Label.ToUpper(CultureInfo.CurrentCulture)), ava.OverviewTiles.Select(t => t.Label));
        Assert.Equal(Loc.P("Main.History.SubtitlePc", 401), wpf.HistorySubtitle);
        Assert.Equal(Loc.P("Main.History.SubtitleDevice", 400), ava.HistorySubtitle);
    }

    [Fact]
    public async Task Only_the_Wpf_host_restores_a_rating_from_history_before_a_live_snapshot()
    {
        var history = History();
        Assert.True(history.Add(Game(0) with { Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 2, RatingAfter = 6500 }));
        var wpf = new WpfMain(history, new AppSettings());
        var ava = new AvaMain(history, new AppSettings());
        await wpf.RefreshAsync();
        await ava.RefreshAsync();

        Assert.Equal(6500.ToString("N0", CultureInfo.CurrentCulture), wpf.SoloRating);
        Assert.Equal("—", ava.SoloRating);
        Assert.Equal(wpf.RatingPoints, ava.RatingPoints);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Home_shows_the_Battlegrounds_cards_only_once_there_are_Battlegrounds_games(bool wpf)
    {
        var history = History();
        Assert.True(history.Add(Game(0)));
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        await (Task)model.RefreshAsync();
        Assert.False((bool)model.HasBgGames);

        Assert.True(history.Add(Game(1) with { Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 3 }));
        await (Task)model.RefreshAsync();
        Assert.True((bool)model.HasBgGames);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deck_matchup_filter_survives_a_refresh_but_resets_for_another_family(bool wpf)
    {
        var history = History();
        dynamic owner = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        dynamic model = wpf ? (object)new Hearthpulse.App.Main.Decks.DeckDetailsViewModel(owner)
            : new Hearthpulse.UI.Main.Decks.DeckDetailsViewModel(owner);
        var games = new[] { Game(0), Game(1, opponent: "MAGE"), Game(2, "Second deck"), Game(3, "Second deck", "MAGE") };
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);
        var first = families.Single(f => f.Name == "First deck");
        var second = families.Single(f => f.Name == "Second deck");
        await Changed((INotifyPropertyChanged)model, "Games", () => model.Show(first, families, Array.Empty<MergeSuggestion>(), null, CardDb.Empty));
        Assert.Equal(2, ((System.Collections.ICollection)model.Games).Count);
        await Changed((INotifyPropertyChanged)model, "Games", () => model.SelectMatchup.Execute("WARRIOR"));
        Assert.Single((System.Collections.IEnumerable)model.Games);
        string filtered = model.FilterText;
        Assert.NotEmpty(filtered);
        // Equal rows deliberately do not notify again; the sort notification follows the updated filter text.
        await Changed((INotifyPropertyChanged)model, "SortedByWinRate", () => model.Show(first, families, Array.Empty<MergeSuggestion>(), null, CardDb.Empty));
        Assert.Equal(filtered, (string)model.FilterText);
        Assert.Single((System.Collections.IEnumerable)model.Games);
        await Changed((INotifyPropertyChanged)model, "Games", () => model.Show(second, families, Array.Empty<MergeSuggestion>(), null, CardDb.Empty));
        Assert.Equal(2, ((System.Collections.ICollection)model.Games).Count);
        Assert.Equal(second.Key, (string)model.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Replay_navigation_clamps_frames_and_preserves_position_only_when_requested(bool wpf)
    {
        var history = History();
        dynamic owner = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        dynamic model = wpf ? (object)new Hearthpulse.App.Main.Match.MatchViewModel(owner)
            : new Hearthpulse.UI.Main.Match.MatchViewModel(owner);
        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var game = Game(0);
        var replay = store.Save(game.Key, ReplayFile.Write(new ReplayHeader(),
            ["T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
             "S MAIN_READY", "T 2 O 30", "V O RESOURCES 2", "B", "E 3 O PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -"]));
        Assert.NotNull(replay);
        var page = MatchPage.Load(game with { Replay = replay }, store, CardDb.Empty, null);
        Assert.Null(page.Missing);
        Assert.True(page.FrameCount > 1);
        model.Show(page);
        model.Frame = int.MaxValue;
        Assert.Equal(page.FrameCount - 1, (int)model.Frame);
        model.BottomOpponent = true;
        model.BottomOpponent = false; // Radio uncheck must not switch perspective back.
        Assert.True((bool)model.BottomOpponent);
        model.ShowDecks = true;
        model.ShowDecks = false;
        Assert.True((bool)model.ShowDecks);
        model.Show(page, keepPosition: true);
        Assert.Equal(page.FrameCount - 1, (int)model.Frame);
        Assert.True((bool)model.BottomOpponent);
        Assert.True((bool)model.ShowDecks);
        model.Show(page);
        Assert.Equal(0, (int)model.Frame);
        Assert.True((bool)model.BottomLocal);
        Assert.True((bool)model.ShowReplay);
    }

    private static BattlegroundsView Fight() => new(9, 4, 6, "TB_BaconShop_HERO_72", 5, 2, 6,
        false, true, false, [], ["BEAST", "DRAGON"], true, [], [], [], [], null, 0, [], [])
    { CombatOdds = new CombatOdds(0.6, 0.1, 0.3, 0) { Runs = 1000 } };

    [Fact]
    public void Overlay_odds_keep_identical_values_and_the_hosts_original_binding_types()
    {
        var snapshot = LiveSnapshot.Empty with { Battlegrounds = Fight(), InGame = true, HearthstoneRunning = true };
        var wpf = new WpfOverlay();
        var ava = new AvaOverlay();
        var settings = new AppSettings { ShowTurnTimer = false, ShowCombatOdds = true };
        wpf.Update(snapshot, CardDb.Empty, settings, null, null);
        ava.Update(snapshot, CardDb.Empty, settings, null, null);

        Assert.True(wpf.ShowOdds);
        Assert.True(ava.ShowOdds);
        Assert.Equal((0.6, 0.1, 0.3), (ava.WinShare, ava.TieShare, ava.LossShare));
        Assert.Equal((ava.WinShare, ava.TieShare, ava.LossShare), (wpf.WinShare.Value, wpf.TieShare.Value, wpf.LossShare.Value));
        Assert.True(wpf.WinShare.IsStar && wpf.TieShare.IsStar && wpf.LossShare.IsStar);
        Assert.Equal((wpf.OddsWin, wpf.OddsTie, wpf.OddsLoss), (ava.OddsWin, ava.OddsTie, ava.OddsLoss));
        settings.ShowCombatOdds = false;
        wpf.Update(snapshot, CardDb.Empty, settings, null, null);
        ava.Update(snapshot, CardDb.Empty, settings, null, null);
        Assert.False(wpf.ShowOdds);
        Assert.False(ava.ShowOdds);
    }

    private static readonly CardDb ArenaCards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
        [
          {"id":"BIG","dbfId":11,"name":"Big","cost":6,"type":"MINION","cardClass":"WARLOCK","collectible":true},
          {"id":"SPELL","dbfId":12,"name":"Spell","cost":2,"type":"SPELL","cardClass":"WARLOCK","collectible":true},
          {"id":"ONE","dbfId":13,"name":"One","cost":1,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"HERO_07","dbfId":893,"name":"Gul'dan","type":"HERO","cardClass":"WARLOCK","collectible":true},
          {"id":"HERO_07bp","dbfId":1,"name":"Life Tap","type":"HERO_POWER","cardClass":"WARLOCK"},
          {"id":"HERO_08bp","dbfId":2,"name":"Fireblast","type":"HERO_POWER","cardClass":"MAGE"}
        ]
        """)));

    [Fact]
    public void Overlay_arena_badges_show_the_same_fit_in_both_hosts_and_hide_for_hero_powers()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var meta = new Hearthpulse.Core.Arena.ArenaMeta(
            [new("BIG", 11, 50, null, null, 0), new("SPELL", 12, 30, null, null, 0), new("ONE", 13, 20, null, null, 0)], [], underground: true);
        var state = new Hearthpulse.Core.Arena.ArenaDraftState("HERO_07", 0, ["SPELL", "SPELL", "ONE"], "DRAFTING");
        var view = Hearthpulse.Core.Arena.ArenaDraft.Build(state, ["BIG", "SPELL"], meta, ArenaCards, underground: false);
        var snapshot = LiveSnapshot.Empty with { Scene = "DRAFT", HearthstoneRunning = true, ArenaDraft = view };
        var settings = new AppSettings { ShowArenaDraft = true };
        var wpf = new WpfOverlay();
        var ava = new AvaOverlay();
        wpf.Update(snapshot, ArenaCards, settings, null, null);
        ava.Update(snapshot, ArenaCards, settings, null, null);

        Assert.True(wpf.ShowArenaPicks && ava.ShowArenaPicks);
        Assert.Equal(wpf.ArenaPicks.Select(b => (b.Number, b.Caption, b.FitCost, b.FitType)), ava.ArenaPicks.Select(b => (b.Number, b.Caption, b.FitCost, b.FitType)));
        Assert.Equal(("Best pick", "6 mana · 0 in deck", "Minion · 1 in deck"), (ava.ArenaPicks[0].Caption, ava.ArenaPicks[0].FitCost, ava.ArenaPicks[0].FitType));
        Assert.Equal("2 mana · 2 in deck", ava.ArenaPicks[1].FitCost);
        Assert.Equal([2, 6], ava.ArenaCurve.Select((bar, cost) => (bar, cost)).Where(b => b.bar.Offered).Select(b => b.cost));
        Assert.Equal(wpf.ArenaCurve.Select(b => (b.Label, b.Count, b.Height, b.Offered)), ava.ArenaCurve.Select(b => (b.Label, b.Count, b.Height, b.Offered)));
        Assert.Equal("1 minion · 2 spells", ava.ArenaTypes);
        Assert.Equal((ava.ArenaMode, ava.ArenaSliceNote), (wpf.ArenaMode, wpf.ArenaSliceNote));
        Assert.Equal("Normal Arena", ava.ArenaMode);
        Assert.Equal("Scores come from Underground Arena games", ava.ArenaSliceNote);

        // The hero power pick: no numbers over the cards, the panel asks for the pick.
        var power = Hearthpulse.Core.Arena.ArenaDraft.Build(state with { Cards = [] }, ["HERO_07bp", "HERO_08bp"], meta, ArenaCards);
        foreach (dynamic model in new object[] { wpf, ava })
        {
            model.Update(snapshot with { ArenaDraft = power }, ArenaCards, settings, null, null);
            Assert.False((bool)model.ShowArenaPicks);
            Assert.True((bool)model.ShowArenaDraft);
            Assert.Equal("Choose a hero power", (string)model.ArenaDraftTitle);
        }
    }

    [Fact]
    public void Wpf_tribe_focus_still_selects_and_clears_the_requested_tribe()
    {
        var model = new WpfOverlay();
        model.Update(LiveSnapshot.Empty with { Battlegrounds = Fight(), InGame = true }, CardDb.Empty, new AppSettings(), null, null);
        var requested = new List<string?>();
        model.FocusRequested += requested.Add;
        Assert.Equal(3, model.FocusChoices.Count);
        model.FocusTribe.Execute("BEAST");
        Assert.Equal("BEAST", Assert.Single(model.FocusChoices, c => c.Selected).Key);
        model.FocusTribe.Execute("BEAST");
        Assert.Equal(string.Empty, Assert.Single(model.FocusChoices, c => c.Selected).Key);
        Assert.Equal(new string?[] { "BEAST", null }, requested);
    }

    [Fact]
    public void Both_overlays_show_the_same_pick_hint_and_drop_it_once_the_choice_closes()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var pool = new Core.CardPool.CardPoolView("CORE_YOP_001", "Illidari Studies", Core.CardPool.PoolTrigger.Choice, "Discover", "Outcast cards · Standard",
            3, new Core.CardPool.PoolHistogram("Cost", [], 0, 0), null, null, [], null, null, []);
        var options = new Dictionary<string, Core.Pool.PickOption> { ["CARD_A"] = new(200, 120, 70), ["CARD_B"] = new(180, 40, 15), ["CARD_C"] = new(10, 5, 2) };
        var stats = new Core.Pool.DiscoverPickStats("standard", 900, new Dictionary<string, Core.Pool.SourcePicks> { ["CORE_YOP_001"] = new(300, options) });
        var picks = Core.CardPool.DiscoverPicks.Build(stats, "CORE_YOP_001", ["CARD_A", "CARD_B", "CARD_C"], CardDb.Empty);
        var constructed = new ConstructedView(5, null, false, "HERO_10", "DEMONHUNTER", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            CardPool = pool,
            DiscoverPicks = picks,
        };
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
        var wpf = new WpfOverlay();
        var ava = new AvaOverlay();
        var settings = new AppSettings();
        wpf.Update(snapshot, CardDb.Empty, settings, null, null);
        ava.Update(snapshot, CardDb.Empty, settings, null, null);

        var rows = wpf.CardPoolPicks.Select(r => (r.CardId, r.PickText, r.WinText, r.Top)).ToList();
        Assert.Equal(rows, ava.CardPoolPicks.Select(r => (r.CardId, r.PickText, r.WinText, r.Top)));
        Assert.Equal([("CARD_A", "60%", "58% won", true), ("CARD_B", "22%", "38% won", false), ("CARD_C", "—", "", false)], rows);
        Assert.Equal("300 choices from this card · Hearthpulse stats pool, 30 days", wpf.CardPoolPicksNote);
        Assert.Equal(wpf.CardPoolPicksNote, ava.CardPoolPicksNote);

        // The pick is made: the session hands no hint any more, and the rows go.
        var closed = snapshot with { Constructed = constructed with { DiscoverPicks = null } };
        wpf.Update(closed, CardDb.Empty, settings, null, null);
        ava.Update(closed, CardDb.Empty, settings, null, null);
        Assert.Empty(wpf.CardPoolPicks);
        Assert.Empty(ava.CardPoolPicks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Observable_retains_equal_value_rows_and_notifies_when_rows_actually_change(bool wpf)
    {
        IRows model = wpf ? new WpfRows() : new AvaRows();
        var names = new List<string?>();
        model.PropertyChanged += (_, e) => names.Add(e.PropertyName);
        var original = new[] { new ValueRow("one") };
        model.Rows = original;
        model.Rows = [new ValueRow("one")];
        Assert.Same(original, model.Rows);
        Assert.Equal(new[] { "Rows" }, names);
        model.Rows = [new ValueRow("two")];
        Assert.Equal("two", Assert.Single(model.Rows).Name);
        Assert.Equal(new[] { "Rows", "Rows" }, names);
    }

    public sealed record ValueRow(string Name);

    private static async Task Changed(INotifyPropertyChanged model, string property, Action change)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property) completion.TrySetResult();
        }
        model.PropertyChanged += OnChange;
        try
        {
            change();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { model.PropertyChanged -= OnChange; }
    }

    private interface IRows : INotifyPropertyChanged { IReadOnlyList<ValueRow> Rows { get; set; } }
    private sealed class WpfRows : Hearthpulse.App.Infrastructure.Observable, IRows
    {
        private IReadOnlyList<ValueRow> _rows = [];
        public IReadOnlyList<ValueRow> Rows { get => _rows; set => Set(ref _rows, value); }
    }
    private sealed class AvaRows : Hearthpulse.UI.Infrastructure.Observable, IRows
    {
        private IReadOnlyList<ValueRow> _rows = [];
        public IReadOnlyList<ValueRow> Rows { get => _rows; set => Set(ref _rows, value); }
    }
}
