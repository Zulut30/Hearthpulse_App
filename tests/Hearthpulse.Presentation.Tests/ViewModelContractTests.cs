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
        Assert.Equal("from Underground Arena games", ava.ArenaSliceNote);
        // One footer: the source, then which games the scores come from.
        Assert.Equal((wpf.ArenaSource, "Scores: Hearthpulse data · from Underground Arena games"), (ava.ArenaSource, ava.ArenaSource));
        // A scored card that is not the best pick needs no caption under its number.
        Assert.Equal(string.Empty, ava.ArenaPicks[1].Caption);
        Assert.Empty(ava.ArenaClassStats);

        // The run's class keeps its community numbers in the panel after the hero pick.
        var withClasses = new Hearthpulse.Core.Arena.ArenaMeta(meta.Cards, [new("WARLOCK", 0.552, 0.8, 0.15, 100), new("MAGE", 0.56, 0.7, 0.16, 100)]);
        var classView = Hearthpulse.Core.Arena.ArenaDraft.Build(state, ["BIG", "SPELL"], withClasses, ArenaCards);
        wpf.Update(snapshot with { ArenaDraft = classView }, ArenaCards, settings, null, null);
        ava.Update(snapshot with { ArenaDraft = classView }, ArenaCards, settings, null, null);
        Assert.Equal(wpf.ArenaClassStats.Select(s => (s.Value, s.Label, s.Tier)), ava.ArenaClassStats.Select(s => (s.Value, s.Label, s.Tier)));
        // Three short figures instead of one sentence that wrapped between "7+ wins:" and its number.
        Assert.Equal(
            [(0.552.ToString("P1", System.Globalization.CultureInfo.CurrentCulture), "win rate"), ("2/2", "class rank"), (0.15.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), "7+ wins")],
            ava.ArenaClassStats.Select(s => (s.Value, s.Label)));

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
        var choices = new Core.CardPool.CardChoices("CORE_YOP_001", "standard", "legend",
            [new("CARD_A", 0, 0.6, 0.583, false), new("CARD_B", 0, 0.222, 0.375, false), new("CARD_D", 0, 0.5, 0.4, true)], null);
        var picks = Core.CardPool.DiscoverPicks.Build(choices, "CORE_YOP_001", ["CARD_A", "CARD_B", "CARD_C", "CARD_D"], CardDb.Empty);
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

        var rows = wpf.CardPoolPicks.Select(r => (r.CardId, r.PickText, r.WinText, r.Top, r.LowSample)).ToList();
        Assert.Equal(rows, ava.CardPoolPicks.Select(r => (r.CardId, r.PickText, r.WinText, r.Top, r.LowSample)));
        static string P(double share) => share.ToString("P0", CultureInfo.CurrentCulture);
        Assert.Equal([("CARD_A", P(0.6), P(0.583) + " won", true, false), ("CARD_B", P(0.222), P(0.375) + " won", false, false), ("CARD_C", "—", "", false, false), ("CARD_D", P(0.5), P(0.4) + " won", false, true)], rows);
        Assert.Equal("Legend · Hearthpulse data · faded: few games", wpf.CardPoolPicksNote);
        Assert.Equal(wpf.CardPoolPicksNote, ava.CardPoolPicksNote);

        // The pick is made: the session hands no hint any more, and the rows go.
        var closed = snapshot with { Constructed = constructed with { DiscoverPicks = null } };
        wpf.Update(closed, CardDb.Empty, settings, null, null);
        ava.Update(closed, CardDb.Empty, settings, null, null);
        Assert.Empty(wpf.CardPoolPicks);
        Assert.Empty(ava.CardPoolPicks);
    }

    [Fact]
    public void Both_overlays_dim_unknown_hand_cards_and_fold_identical_secret_lines()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var secrets = new Core.Secrets.SecretsView(
            [new(11, "MAGE", 3, 3, null, null, 3), new(12, "MAGE", 3, 3, null, null, 3), new(13, "MAGE", 5, 2, null, null, 4)], [])
        {
            Pool = "Mage · Standard",
        };
        var constructed = new ConstructedView(6, null, false, "HERO_10", "DEMONHUNTER", "HERO_08", "MAGE", null, [], 20, 4, [],
            [new(1, 1, null, null, false, false, null), new(2, 0, null, null, true, false, null), new(3, 2, null, null, false, false, "Arcane Intellect"),
             new(4, 0, null, null, false, true, null)], 18, 4, 3, null, null)
        {
            Secrets = secrets,
        };
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
        var wpf = new WpfOverlay();
        var ava = new AvaOverlay();
        var settings = new AppSettings();
        wpf.Update(snapshot, CardDb.Empty, settings, null, null);
        ava.Update(snapshot, CardDb.Empty, settings, null, null);

        // Only the card nothing is known about is drawn faint; kept, created and the Coin keep their colour.
        var hand = wpf.Hand.Select(h => (h.Slot, h.Highlight, h.Unknown)).ToList();
        Assert.Equal([("1", false, true), ("2", false, false), ("3", false, false), ("4", true, false)], hand);
        Assert.Equal(hand, ava.Hand.Select(h => (h.Slot, h.Highlight, h.Unknown)));
        // ...and shows only its slot and turn: no column of "Unknown".
        Assert.Equal((string.Empty, string.Empty), (wpf.Hand[0].Detail, ava.Hand[0].Detail));
        Assert.NotEmpty(ava.Hand[2].Detail);

        // Two Secrets played on turn 3 for 3 mana read the same, so they share one line.
        var lines = wpf.SecretSlots.Select(s => s.Text).ToList();
        Assert.Equal(["×2 · turn 3 · 3 mana · 3 left", "turn 5 · 2 mana · 4 left"], lines);
        Assert.Equal(lines, ava.SecretSlots.Select(s => s.Text));
        Assert.Equal("Secrets · 3", wpf.SecretsTitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overlay_archetype_closes_until_the_game_ends_or_the_opponent_changes(bool wpf)
    {
        var odds = new Core.Constructed.OpponentArchetypeView(
            [new("Aggro Warrior", "Aggro Warrior", 0.7, 0.55, 900), new("Control Warrior", "Control Warrior", 0.3, 0.5, 400)],
            [new("CS2_108", "Execute", 2, 0.8)], 3, "Standard");
        var constructed = new ConstructedView(5, null, false, "HERO_10", "DEMONHUNTER", "HERO_01", "WARRIOR", "Rival#1234", [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            OpponentArchetype = odds,
        };
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
        var settings = new AppSettings { ShowOpponentArchetype = true };
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        void Show(LiveSnapshot s) => model.Update(s, CardDb.Empty, settings, null, null);

        Show(snapshot);
        Assert.True((bool)model.ShowArchetype);
        Assert.Equal("CS2_108", (string)model.LikelyCards[0].CardId);

        // Closed: hidden for the rest of this game, the setting stays on.
        ((System.Windows.Input.ICommand)model.DismissArchetype).Execute(null);
        Assert.False((bool)model.ShowArchetype);
        Show(snapshot with { Constructed = constructed with { Turn = 9 } });
        Assert.False((bool)model.ShowArchetype);
        Assert.True(settings.ShowOpponentArchetype);

        // The game ends, the next one shows the panel again.
        Show(snapshot with { InGame = false, Constructed = null });
        Assert.False((bool)model.ShowArchetype);
        Show(snapshot with { Constructed = constructed with { Turn = 1 } });
        Assert.True((bool)model.ShowArchetype);

        // A new game that starts without a gap in the updates: the turn goes back.
        ((System.Windows.Input.ICommand)model.DismissArchetype).Execute(null);
        Show(snapshot with { Constructed = constructed with { Turn = 4 } });
        Assert.False((bool)model.ShowArchetype);
        Show(snapshot with { Constructed = constructed with { Turn = 2 } });
        Assert.True((bool)model.ShowArchetype);

        // Another opponent shows it again too.
        ((System.Windows.Input.ICommand)model.DismissArchetype).Execute(null);
        Show(snapshot with { Constructed = constructed with { Turn = 6, OpponentName = "Other#5678" } });
        Assert.True((bool)model.ShowArchetype);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Overlay_lists_change_rows_in_place_and_become_a_new_list_only_into_or_out_of_empty(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        static LiveSnapshot With(params HandSlot[] hand) => LiveSnapshot.Empty with
        {
            Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED",
            Constructed = new ConstructedView(6, null, false, "HERO_10", "DEMONHUNTER", "HERO_08", "MAGE", null, [], 20, 4, [], hand, 18, hand.Length, 0, null, null),
        };

        INotifyPropertyChanged vm = wpf ? new WpfOverlay() : new AvaOverlay();
        var settings = new AppSettings();
        Action<LiveSnapshot> update = wpf ? s => ((WpfOverlay)vm).Update(s, CardDb.Empty, settings, null, null) : s => ((AvaOverlay)vm).Update(s, CardDb.Empty, settings, null, null);
        System.Collections.IList Hand() => wpf ? (System.Collections.IList)((WpfOverlay)vm).Hand : (System.Collections.IList)((AvaOverlay)vm).Hand;
        var names = new List<string?>();
        vm.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        update(With(new(1, 1, null, null, false, false, null), new(2, 0, null, null, true, false, null), new(3, 2, null, null, false, false, null)));
        var list = Hand();
        var kept = list[0];
        Assert.Contains("Hand", names);

        // The third card turns out to be a known one: only its row changes, the list and the other rows stay the objects
        // the items control already shows.
        names.Clear();
        update(With(new(1, 1, null, null, false, false, null), new(2, 0, null, null, true, false, null), new(3, 2, null, null, false, false, "Arcane Intellect")));
        Assert.Same(list, Hand());
        Assert.Same(kept, Hand()[0]);
        Assert.Equal(3, Hand().Count);
        Assert.DoesNotContain("Hand", names);

        // Out of empty and back there is no row to keep: a new list, announced, so "shown when not empty" looks again.
        update(With());
        Assert.Empty(Hand());
        Assert.NotSame(list, Hand());
        Assert.Contains("Hand", names);
    }

    [Fact]
    public void SyncList_ends_equal_to_any_target_and_keeps_the_rows_that_did_not_change()
    {
        var random = new Random(5);
        for (var run = 0; run < 500; run++)
        {
            var list = new Hearthpulse.App.Infrastructure.SyncList<ValueRow>(Enumerable.Range(0, random.Next(8)).Select(_ => new ValueRow(random.Next(5).ToString(CultureInfo.InvariantCulture))));
            var target = Enumerable.Range(0, random.Next(8)).Select(_ => new ValueRow(random.Next(5).ToString(CultureInfo.InvariantCulture))).ToList();
            var before = list.ToList();
            Assert.Equal(!before.SequenceEqual(target), list.Sync(target));
            Assert.Equal(target, list);
            Assert.False(list.Sync(target));
        }

        // One row appended or one row changed touches only that row.
        var rows = new Hearthpulse.App.Infrastructure.SyncList<ValueRow>([new("a"), new("b"), new("c")]);
        var first = rows[0];
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        Assert.True(rows.Sync([new("a"), new("b"), new("c"), new("d")]));
        Assert.True(rows.Sync([new("a"), new("B"), new("c"), new("d")]));
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add, System.Collections.Specialized.NotifyCollectionChangedAction.Replace], events);
        Assert.Same(first, rows[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Keyboard_navigation_maps_digits_to_pages_and_Back_leaves_only_the_page_on_screen(bool wpf)
    {
        Assert.Equal(WpfMain.NavPages, AvaMain.NavPages);
        Assert.Equal("Overview", WpfMain.NavPageForDigit(1));
        Assert.Equal("History", AvaMain.NavPageForDigit(6));
        Assert.Equal("Settings", WpfMain.NavPageForDigit(7));
        Assert.Null(AvaMain.NavPageForDigit(0));
        Assert.Null(WpfMain.NavPageForDigit(8));

        var history = History();
        Assert.True(history.Add(Game(0)));
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        await model.RefreshAsync();
        Assert.False((bool)model.GoBack("History"));

        Assert.True(await (Task<bool>)model.OpenMatchPageAsync("presentation-game-0"));
        Assert.True((bool)model.MatchOpen);
        // The match page lives on History: Esc on another page leaves it open.
        Assert.False((bool)model.GoBack(ConstructedPage.Standard));
        Assert.True((bool)model.MatchOpen);
        Assert.True((bool)model.GoBack("History"));
        Assert.False((bool)model.MatchOpen);
    }

    [Fact]
    public void Layout_mode_snaps_dragged_panels_to_the_edges_and_centre_and_names_their_size()
    {
        foreach (var snap in new Func<double, double, double, double>[] { Hearthpulse.App.Overlay.PanelSnap.Snap, Hearthpulse.UI.Overlay.PanelSnap.Snap })
        {
            Assert.Equal(12, snap(5, 200, 1920));          // left gutter
            Assert.Equal(1920 - 12 - 200, snap(1700, 200, 1920)); // right gutter
            Assert.Equal(860, snap(853, 200, 1920));       // centre line
            Assert.Equal(400, snap(400, 200, 1920));       // free spot stays
            Assert.Equal(0, snap(0, 200, 1920));           // flush to the edge is still allowed
            Assert.Equal(30, snap(30, 1900, 1920));        // wider than the guides: no snapping
        }

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(string.Empty, Hearthpulse.App.Overlay.PanelSnap.ScaleText(1));
            Assert.Equal("115 %", Hearthpulse.UI.Overlay.PanelSnap.ScaleText(1.15));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

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
