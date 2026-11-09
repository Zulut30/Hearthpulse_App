using System.Text;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// The Underground redraft (docs/arena-draft.md, "Draft state"): a synthetic run in the line formats of the real
/// Arena.log (Fixtures/Arena/redraft-synthetic.log). No live log with <c>REDRAFTING</c> exists yet, so the rule is
/// checked, not a client format: after the redraft the client's next list is the deck's base, only the redraft's
/// picks are added to it, the cards taken out never come back, and the deck a game of the run starts with is the truth.
/// <para>
/// The run: Gul'dan; 23 commons (TST_C02 taken twice in a row), a legendary TST_L01 logged twice (one pick), five cards
/// TST_R01..05 the redraft takes out = 30; listed once; then REDRAFTING, five new picks TST_N01..05, and the client
/// lists the 25 kept cards with the five new ones.
/// </para>
/// </summary>
public sealed class ArenaDraftRedraftTests
{
    private const long DeckId = 900000001;

    private static readonly string[] Removed = ["TST_R01", "TST_R02", "TST_R03", "TST_R04", "TST_R05"];

    private static readonly string[] Added = ["TST_N01", "TST_N02", "TST_N03", "TST_N04", "TST_N05"];

    private static readonly CardDb Cards = MakeCards();

    private static string[] Log() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arena", "redraft-synthetic.log"));

    private static int RedraftAt(IReadOnlyList<string> log) => log.ToList().FindIndex(l => l.Contains("REDRAFTING", StringComparison.Ordinal));

    private static ArenaDraftTracker Read(IEnumerable<string> lines, CardDb? cards = null)
    {
        var tracker = new ArenaDraftTracker();
        foreach (var line in lines)
        {
            tracker.Feed(line, cards ?? Cards);
        }

        return tracker;
    }

    private static Dictionary<string, int> Counts(IEnumerable<string> ids) =>
        ids.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    /// <summary>The 30 cards the run holds after the redraft.</summary>
    private static Dictionary<string, int> AfterRedraft()
    {
        var deck = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i <= 23; i++)
        {
            deck[$"TST_C{i:00}"] = 1;
        }

        deck["TST_C02"] = 2;
        deck["TST_L01"] = 1;
        foreach (var id in Added)
        {
            deck[id] = 1;
        }

        return deck;
    }

    [Fact]
    public void Before_the_redraft_the_run_is_thirty_cards_in_revision_zero()
    {
        var log = Log();
        var state = Read(log.Take(RedraftAt(log))).State;

        Assert.Equal(30, state.Cards.Count);
        Assert.Equal(0, state.Revision);
        Assert.False(state.Incomplete);
        Assert.Equal(2, state.Cards.Count(c => c == "TST_C02"));
        Assert.Single(state.Cards, c => c == "TST_L01");
        Assert.All(Removed, id => Assert.Contains(id, state.Cards));
    }

    /// <summary>AC20: the removed cards are gone from the deck, the curve, the score and the types; the new ones count once.</summary>
    [Fact]
    public void A_redraft_drops_the_removed_cards_and_counts_the_new_ones_once()
    {
        var state = Read(Log()).State;

        Assert.Equal(1, state.Revision);
        Assert.False(state.Incomplete);
        Assert.Equal(DeckId, state.DeckId);
        Assert.Equal(AfterRedraft(), Counts(state.Cards));
        Assert.DoesNotContain(state.Cards, Removed.Contains);

        var view = ArenaDraft.Build(state, (ArenaDraftOffer?)null, null, Cards);
        Assert.Equal(30, view.Picks);
        Assert.Equal(30, view.Score.Curve.Sum());
        Assert.Equal(30, view.Deck.Sum(c => c.Count));
        Assert.DoesNotContain(view.Deck, c => Removed.Contains(c.CardId));
        Assert.All(Added, id => Assert.Equal(1, view.Deck.Single(c => c.CardId == id).Count));
        // The removed cards are the only spells: none is left among the types.
        Assert.Equal(0, view.Types!.Spells);
        Assert.Equal(30, view.Types.Minions);
    }

    /// <summary>AC20, the other way the client may list: only the kept cards, the redraft's picks still to come.</summary>
    [Fact]
    public void A_list_of_the_kept_cards_only_gets_the_redraft_picks_added()
    {
        var log = Log();
        var redraft = RedraftAt(log);
        var lines = log.Where((l, i) => i < redraft || !Added.Any(id => l.EndsWith("contains card " + id, StringComparison.Ordinal)));

        var state = Read(lines).State;

        Assert.Equal(AfterRedraft(), Counts(state.Cards));
        Assert.Equal(1, state.Revision);
        Assert.False(state.Incomplete);
    }

    /// <summary>AC21: a short list after the redraft stays short (27/30) and says so; no card of the earlier revision returns.</summary>
    [Fact]
    public void A_short_list_after_the_redraft_is_not_filled_from_earlier_picks()
    {
        var log = Log();
        var redraft = RedraftAt(log);
        string[] missing = ["TST_C02", "TST_C03", "TST_C04"];
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var lines = log.Where((l, i) => i < redraft
                                        || missing.FirstOrDefault(id => l.EndsWith("contains card " + id, StringComparison.Ordinal)) is not { } id
                                        || !dropped.Add(id));

        var state = Read(lines).State;

        Assert.Equal(27, state.Cards.Count);
        Assert.True(state.Incomplete);
        Assert.Equal(1, state.Revision);
        Assert.DoesNotContain(state.Cards, Removed.Contains);
        Assert.Single(state.Cards, c => c == "TST_C02");
        Assert.DoesNotContain("TST_C03", state.Cards);
        Assert.Equal(27, ArenaDraft.Build(state, (ArenaDraftOffer?)null, null, Cards).Picks);
    }

    /// <summary>Between the redraft and the client's list only the redraft's picks are known, and the deck says it is not whole.</summary>
    [Fact]
    public void Until_the_client_lists_the_deck_a_redraft_shows_only_its_own_picks()
    {
        var log = Log();
        var state = Read(log.Take(RedraftAt(log) + 1 + Added.Length)).State;

        Assert.Equal(Added, state.Cards);
        Assert.True(state.Incomplete);
        Assert.Equal(1, state.Revision);
        Assert.Equal("REDRAFTING", state.Mode);
    }

    [Fact]
    public void The_redraft_mode_logged_again_is_the_same_revision_and_a_second_redraft_the_next()
    {
        var log = Log().ToList();
        var redraft = RedraftAt(log);
        log.Insert(redraft + 1, log[redraft]);
        Assert.Equal(1, Read(log).State.Revision);

        log.Add("D 21:00:00.0000000 SetDraftMode - REDRAFTING");
        var second = Read(log).State;
        Assert.Equal(2, second.Revision);
        Assert.Empty(second.Cards);
        Assert.True(second.Incomplete);
    }

    /// <summary>AC23: the same Arena.log read again (the app restarted, or the lines delivered twice) gives the same state.</summary>
    [Fact]
    public void Reading_the_log_again_gives_the_same_state()
    {
        var log = Log();
        var once = Read(log).State;
        var restarted = Read(log).State;
        var twice = Read(log.Concat(log)).State;

        foreach (var state in new[] { restarted, twice })
        {
            Assert.Equal(Counts(once.Cards), Counts(state.Cards));
            Assert.Equal(once.Revision, state.Revision);
            Assert.Equal(once.Incomplete, state.Incomplete);
            Assert.Equal(once.DeckId, state.DeckId);
            Assert.Equal(once.HeroCardId, state.HeroCardId);
        }
    }

    /// <summary>AC23: a legendary logged twice is one card, a common taken twice in a row is two (the card data is known).</summary>
    [Fact]
    public void The_legendary_repeat_counts_once_and_two_commons_count_twice()
    {
        var log = Log();
        var picks = Read(log.TakeWhile(l => !l.Contains("OnChoicesAndContents", StringComparison.Ordinal)));

        Assert.Single(picks.State.Cards, c => c == "TST_L01");
        Assert.Equal(2, picks.State.Cards.Count(c => c == "TST_C02"));
        Assert.Equal(30, picks.State.Cards.Count);
        // The hero (two lines) and 30 cards; the legendary's second line is no pick.
        Assert.Equal(32, picks.PickCount);
    }

    /// <summary>AC23: the legendary's package added after the pick, and its line logged again, is not added twice.</summary>
    [Fact]
    public void A_package_is_not_added_twice_by_the_repeated_line()
    {
        var tracker = new ArenaDraftTracker();
        tracker.Feed("D 20:00:01.0000000 Client chooses: Gul'dan (HERO_07)", Cards);
        tracker.Feed("D 20:00:02.0000000 Client chooses: Card L01 (TST_L01)", Cards);
        tracker.AddPackage(["TST_C20", "TST_C21"]);
        tracker.Feed("D 20:00:03.0000000 Client chooses: Card L01 (TST_L01)", Cards);

        Assert.Equal(["TST_L01", "TST_C20", "TST_C21"], tracker.State.Cards);
    }

    /// <summary>AC23: the app opened late, the log starts with the client's list: the redraft still applies.</summary>
    [Fact]
    public void A_late_start_from_the_list_gives_the_same_deck()
    {
        var log = Log();
        var state = Read(log.SkipWhile(l => !l.Contains("OnChoicesAndContents", StringComparison.Ordinal))).State;

        Assert.Equal(AfterRedraft(), Counts(state.Cards));
        Assert.Equal(1, state.Revision);
        Assert.Equal(DeckId, state.DeckId);
    }

    /// <summary>A game's deck goes only to the run of its deck id.</summary>
    [Fact]
    public void Another_runs_game_deck_changes_nothing()
    {
        var tracker = Read(Log());
        var before = tracker.State;

        Assert.Null(tracker.ApplyGameDeck(DeckId + 1, Removed, underground: true));
        Assert.Same(before, tracker.State);
        Assert.Null(tracker.State.Variant);
    }

    /// <summary>
    /// AC22: the draft is short after the redraft (27/30); the game of the run starts with the deck Decks.log announced,
    /// which replaces the tracked one (once, logged once), names the run Underground and makes the deck whole.
    /// </summary>
    [Fact]
    public void The_deck_a_game_of_the_run_starts_with_replaces_a_differing_draft()
    {
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory)
        {
            Clock = () => now,
            IsHearthstoneRunning = () => true,
            ReadsMemory = false,
            Cards = Cards,
        };
        session.SetArenaDraftEnabled(true);
        session.OnSessionStarted("Hearthstone_2026_11_09_19_55_00", new DateTime(2026, 11, 9, 19, 55, 0), attachedMidSession: false);

        var log = Log();
        var redraft = RedraftAt(log);
        string[] missing = ["TST_C05", "TST_C06", "TST_C07"];
        session.OnLines("Arena.log", log.Where((l, i) => i < redraft || !missing.Any(id => l.EndsWith("contains card " + id, StringComparison.Ordinal))).ToList(), catchingUp: false);
        Assert.Equal(27, session.ArenaDraftState.Cards.Count);
        Assert.True(session.ArenaDraftState.Incomplete);

        var code = DeckString.Encode(AfterRedraft(), "WARLOCK", "Wild", Cards);
        Assert.NotNull(code);
        session.OnLines("Decks.log",
        [
            "I 20:05:00.0000000 Starting Arena Game With Deck:",
            "I 20:05:00.0000000 ### Warlock",
            $"I 20:05:00.0000000 # Deck ID: {DeckId}",
            "I 20:05:00.0000000 " + code,
        ], catchingUp: false);
        session.OnLines("Power.log", GameStart("GT_UNDERGROUND_ARENA"), catchingUp: false);
        now = now.AddSeconds(1);
        session.OnTick();

        var state = session.ArenaDraftState;
        Assert.Equal(AfterRedraft(), Counts(state.Cards));
        Assert.False(state.Incomplete);
        Assert.Equal(ArenaDraftState.UndergroundVariant, state.Variant);
        Assert.Equal(1, session.ArenaGameDeckCorrections);

        // Checked once per game.
        now = now.AddSeconds(1);
        session.OnTick();
        Assert.Equal(1, session.ArenaGameDeckCorrections);

        // Another redraft, and a game Decks.log announced nothing for: the old announcement brings no removed card back.
        session.OnLines("Arena.log", ["D 20:30:00.0000000 SetDraftMode - REDRAFTING"], catchingUp: false);
        session.OnLines("Power.log", GameStart("GT_UNDERGROUND_ARENA"), catchingUp: false);
        now = now.AddSeconds(1);
        session.OnTick();
        Assert.Equal(2, session.ArenaDraftState.Revision);
        Assert.Empty(session.ArenaDraftState.Cards);
        Assert.True(session.ArenaDraftState.Incomplete);
        Assert.Equal(1, session.ArenaGameDeckCorrections);
    }

    /// <summary>A game's deck equal to the draft changes nothing and logs nothing.</summary>
    [Fact]
    public void A_matching_game_deck_is_no_correction()
    {
        var tracker = Read(Log());

        var check = tracker.ApplyGameDeck(DeckId, AfterRedraft().SelectMany(c => Enumerable.Repeat(c.Key, c.Value)).ToList(), underground: false);

        Assert.NotNull(check);
        Assert.False(check.Differed);
        Assert.Equal(ArenaDraftState.NormalVariant, tracker.State.Variant);
        Assert.False(tracker.State.Incomplete);
    }

    /// <summary>The start of a game of the given type (Power.log), as the client writes it.</summary>
    internal static List<string> GameStart(string gameType) =>
    [
        .. new[]
        {
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            $"DebugPrintGame() - GameType={gameType}",
            "DebugPrintGame() - FormatType=FT_WILD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
        }.Select(l => "D 20:05:10.0000000 GameState." + l),
    ];

    /// <summary>The run's cards: the commons and the new picks are minions, the removed cards spells, one legendary.</summary>
    private static CardDb MakeCards()
    {
        var json = new StringBuilder("[");
        json.Append("""{"id":"HERO_07","dbfId":893,"name":"Gul'dan","type":"HERO","cardClass":"WARLOCK","collectible":true}""");
        var dbf = 50000;
        void Card(string id, string type, string rarity, int cost) =>
            json.Append($$""",{"id":"{{id}}","dbfId":{{++dbf}},"name":"Card {{id[4..]}}","cost":{{cost}},"type":"{{type}}","rarity":"{{rarity}}","cardClass":"NEUTRAL","collectible":true}""");
        for (var i = 1; i <= 23; i++)
        {
            Card($"TST_C{i:00}", "MINION", "COMMON", i % 8);
        }

        Card("TST_L01", "MINION", "LEGENDARY", 8);
        foreach (var id in Removed)
        {
            Card(id, "SPELL", "COMMON", 2);
        }

        foreach (var id in Added)
        {
            Card(id, "MINION", "RARE", 3);
        }

        json.Append(']');
        return CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(json.ToString())));
    }
}
