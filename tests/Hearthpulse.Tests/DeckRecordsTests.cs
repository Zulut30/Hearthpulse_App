using System.Globalization;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The deck panel's record (own games with the deck, else the archetype's meta win rate) and the backfill that gives
/// old games their deck from Decks.log. Log lines and codes come from a real Decks.log (October 2026).
/// </summary>
public sealed class DeckRecordsTests
{
    private const string RogueCode = "AAECAeCqBgiGqAeHqAeIqAflrwe2sgel2AeI2QfDiwgL958E2aIGwZcHtMEHxcEH1cUHq8kHpNgHkdkHjdoHvPIHAAA=";
    private const string HunterCode = "AAECAfX+BwSongbDgweD2gfziQgN4fgF0J4G3v8GtpcHlb8HxN0Hxt0Hx90HteEHt+EHqvoHvoEI+4kIAAA=";

    private static readonly string[] SessionLines =
    [
        "I 21:53:58.1000000 Deck Contents Received:",
        "I 21:53:58.1000000 ### Охотник на демонов",
        "I 21:53:58.1000000 # Deck ID: 2774348028",
        "I 21:53:58.1000000 " + HunterCode,
        "I 21:53:58.1000000 ### Разбойник",
        "I 21:53:58.1000000 # Deck ID: 2774348185",
        "I 21:53:58.1000000 " + RogueCode,
        "I 21:54:50.3229357 Finding Game With Deck:",
        "I 21:54:50.3229357 ### Охотник на демонов",
        "I 21:54:50.3229357 # Deck ID: 2774348028",
        "I 21:54:50.3229357 " + HunterCode,
        "I 21:54:53.4797447 Finding Game With Deck:",
        "I 21:54:53.4797447 ### Разбойник",
        "I 21:54:53.4797447 # Deck ID: 2774348185",
        "I 21:54:53.4797447 " + RogueCode,
        "I 23:58:00.0000000 Finding Game With Deck:",
        "I 23:58:00.0000000 ### Охотник на демонов",
        "I 23:58:00.0000000 # Deck ID: 2774348028",
        "I 23:58:00.0000000 " + HunterCode,
        "I 00:01:10.0000000 Finding Game With Deck:",
        "I 00:01:10.0000000 ### Разбойник",
        "I 00:01:10.0000000 # Deck ID: 2774348185",
        "I 00:01:10.0000000 " + RogueCode,
    ];

    private static readonly DateOnly Day = new(2026, 10, 22);

    private static string? ClassOf(string code) => code == RogueCode ? "ROGUE" : code == HunterCode ? "DEMONHUNTER" : null;

    private static GameRecord Game(string key, DateTime start, string playerClass, string result = "Win", string? deck = null, string? code = null, string mode = "Ranked") =>
        new()
        {
            Key = key,
            StartedAt = start,
            EndedAt = start.AddMinutes(8),
            Mode = mode,
            GameType = mode == "Battlegrounds" ? "GT_BATTLEGROUNDS" : "GT_RANKED",
            Format = "FT_STANDARD",
            PlayerName = "velox#21276",
            PlayerClass = playerClass,
            Result = result,
            DeckName = deck,
            DeckCode = code,
            Source = "backfill",
        };

    [Fact]
    public void Decks_log_blocks_are_read_with_their_time_and_kind()
    {
        var entries = DecksLog.Parse(SessionLines, Day);

        Assert.Equal(6, entries.Count);
        Assert.Equal(2, entries.Count(e => !e.FindingGame));
        var finding = entries[3];
        Assert.True(finding.FindingGame);
        Assert.Equal("Разбойник", finding.Name);
        Assert.Equal(2774348185, finding.DeckId);
        Assert.Equal(RogueCode, finding.Code);
        Assert.Equal(new DateTime(2026, 10, 22, 21, 54, 53).AddTicks(4797447), finding.At);
        // Past midnight the date moves on.
        Assert.Equal(new DateTime(2026, 10, 23, 0, 1, 10), entries[^1].At);
    }

    [Fact]
    public void Every_code_of_a_deck_id_is_collected()
    {
        var edited = SessionLines.Append("I 00:30:00.0000000 Deck Contents Received:")
            .Append("I 00:30:00.0000000 ### Разбойник")
            .Append("I 00:30:00.0000000 # Deck ID: 2774348185")
            .Append("I 00:30:00.0000000 AAECAeCqBgAAAAA=");

        var codes = DecksLog.CodesOf(DecksLog.Parse(edited, Day), 2774348185);

        Assert.Equal(2, codes.Count);
        Assert.Contains(RogueCode, codes);
        Assert.Empty(DecksLog.CodesOf(DecksLog.Parse(edited, Day), 0));
    }

    [Fact]
    public void Old_games_get_the_deck_the_client_queued_with()
    {
        var entries = DecksLog.Parse(SessionLines, Day);
        var games = new[]
        {
            // Re-queued from the Hunter deck to the Rogue one three seconds later: the Rogue deck played.
            Game("a", new DateTime(2026, 10, 22, 21, 55, 15), "ROGUE"),
            // The next Finding is Hunter at 23:58, but this game is Rogue: a wrong class is never filled in.
            Game("b", new DateTime(2026, 10, 22, 23, 58, 30), "ROGUE"),
            // Past midnight.
            Game("c", new DateTime(2026, 10, 23, 0, 1, 40), "ROGUE"),
            // Hours after the last Finding: too long for a queue.
            Game("d", new DateTime(2026, 10, 23, 3, 0, 0), "ROGUE"),
            // Already has a deck code: untouched.
            Game("e", new DateTime(2026, 10, 23, 3, 30, 0), "ROGUE", code: RogueCode),
        };

        var fills = DeckRecords.MatchFindings(games, entries, ClassOf);

        Assert.Equal(["a", "c"], fills.Select(f => f.GameKey));
        Assert.All(fills, f => Assert.Equal(RogueCode, f.DeckCode));
        Assert.All(fills, f => Assert.Equal("Разбойник", f.DeckName));
    }

    [Fact]
    public void A_finding_is_not_reused_for_a_second_game()
    {
        var entries = DecksLog.Parse(SessionLines.Take(15), Day);
        var games = new[]
        {
            Game("first", new DateTime(2026, 10, 22, 21, 55, 15), "ROGUE"),
            // A Battlegrounds game came between, and this one has no Finding of its own.
            Game("bg", new DateTime(2026, 10, 22, 22, 0, 0), "NEUTRAL", mode: "Battlegrounds"),
            Game("second", new DateTime(2026, 10, 22, 22, 5, 0), "ROGUE"),
        };

        Assert.Equal(["first"], DeckRecords.MatchFindings(games, entries, ClassOf).Select(f => f.GameKey));
    }

    [Fact]
    public void Filling_keeps_saved_names_and_codes()
    {
        var db = Path.Combine(Path.GetTempPath(), $"hp-deckfill-{Guid.NewGuid():N}.db");
        var history = new HistoryStore(db);
        history.Add(Game("a", new DateTime(2026, 10, 22, 21, 55, 15), "ROGUE"));
        history.Add(Game("b", new DateTime(2026, 10, 22, 22, 10, 0), "ROGUE", deck: "My Rogue"));
        history.Add(Game("c", new DateTime(2026, 10, 22, 22, 20, 0), "ROGUE", deck: "Kept", code: HunterCode));

        var changed = history.FillDecks(
        [
            new DeckFill("a", "Разбойник", RogueCode),
            new DeckFill("b", "Разбойник", RogueCode),
            new DeckFill("c", "Разбойник", RogueCode),
        ]);

        Assert.Equal(2, changed);
        var games = history.Recent().ToDictionary(g => g.Key);
        Assert.Equal(("Разбойник", RogueCode), (games["a"].DeckName, games["a"].DeckCode));
        Assert.Equal(("My Rogue", RogueCode), (games["b"].DeckName, games["b"].DeckCode));
        Assert.Equal(("Kept", HunterCode), (games["c"].DeckName, games["c"].DeckCode));
    }

    [Fact]
    public void Own_record_counts_the_deck_by_code_older_lists_and_name()
    {
        const string olderList = "AAECAeCqBgiGqAeHqAeIqAflrwe2sgel2AeI2QfDiwgL958E2aIGwZcHtMEHxcEH1cUHq8kHpNgHkdkHjdoHAAA=";
        var start = new DateTime(2026, 10, 22, 12, 0, 0);
        var games = new[]
        {
            Game("1", start, "ROGUE", "Win", "Разбойник", RogueCode),
            Game("2", start.AddHours(1), "ROGUE", "Loss", "Renamed", olderList),
            Game("3", start.AddHours(2), "ROGUE", "Win", "Разбойник"),
            // Same name, other list: another deck that happens to share the default name.
            Game("4", start.AddHours(3), "ROGUE", "Loss", "Разбойник", HunterCode),
            Game("5", start.AddHours(4), "ROGUE", "Win", "Разбойник", mode: "Battlegrounds"),
        };

        var record = DeckRecords.Find(games, "Разбойник", RogueCode, "ROGUE", [olderList]);

        Assert.NotNull(record);
        Assert.Equal((3, 2, 1), (record.Games, record.Wins, record.Losses));
        Assert.Null(DeckRecords.Find(games, "Other", "AAECAQcAAAA=", "WARRIOR"));
    }

    [Fact]
    public void Same_list_in_another_card_order_is_the_same_deck()
    {
        var deck = DeckString.TryDecode(RogueCode, "x", 0)!;
        var reordered = Encode(deck.HeroDbfIds[0], deck.Cards.OrderByDescending(c => c.Key));
        Assert.NotEqual(RogueCode, reordered);

        var record = DeckRecords.Find([Game("1", DateTime.Today, "ROGUE", code: reordered)], "Разбойник", RogueCode, "ROGUE");

        Assert.Equal(1, record?.Games);
    }

    [Fact]
    public void Deck_class_comes_from_the_code_hero()
    {
        var hero = DeckString.TryDecode(RogueCode, "x", 0)!.HeroDbfIds[0];
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""[{"id":"HERO_03","dbfId":{{hero}},"name":"Valeera","type":"HERO","cardClass":"ROGUE"}]""")));

        Assert.Equal("ROGUE", DeckRecords.ClassOf(RogueCode, cards));
        Assert.Null(DeckRecords.ClassOf("not a code", cards));
    }

    [Fact]
    public void A_deck_name_ending_with_a_colon_is_not_a_block_header()
    {
        var entries = DecksLog.Parse(
        [
            "I 21:54:50.3229357 Finding Game With Deck:",
            "I 21:54:50.3229357 ### Aggro:",
            "I 21:54:50.3229357 # Deck ID: 42",
            "I 21:54:50.3229357 " + RogueCode,
        ], Day);

        var entry = Assert.Single(entries);
        Assert.True(entry.FindingGame);
        Assert.Equal("Aggro:", entry.Name);
        Assert.Equal(42, entry.DeckId);
    }

    [Fact]
    public void Matchup_counts_the_same_games_as_the_record()
    {
        var start = new DateTime(2026, 10, 22, 12, 0, 0);
        GameRecord Vs(string key, int hour, string opponent, string result, string? code) =>
            Game(key, start.AddHours(hour), "ROGUE", result, "Разбойник", code) with { OpponentClass = opponent };
        var games = DeckRecords.Matching(
        [
            Vs("1", 0, "MAGE", "Win", RogueCode),
            Vs("2", 1, "MAGE", "Loss", null),
            Vs("3", 2, "MAGE", "Win", HunterCode),
            Vs("4", 3, "PRIEST", "Win", RogueCode),
        ], "Разбойник", RogueCode, "ROGUE");

        var matchup = DeckRecords.Matchup(games, "MAGE");

        Assert.Equal((2, 1, 1), (matchup?.Games, matchup?.Wins, matchup?.Losses));
        Assert.Null(DeckRecords.Matchup(games, "WARRIOR"));
    }

    [Fact]
    public void Header_prefers_own_games_then_meta()
    {
        var meta = new DeckMetaStat("Cycle Rogue", 0.534, 12_345, "Legend", "Firestone");

        Assert.Equal($"3–2 · {0.6.ToString("P0", CultureInfo.CurrentCulture)} win rate", DeckHeader.Describe(new DeckAggregate("D", null, "ROGUE", 5, 3, 2, DateTime.Today), meta).Text);
        var fromMeta = DeckHeader.Describe(null, meta);
        Assert.Equal($"Meta {0.534.ToString("P0", CultureInfo.CurrentCulture)} · Legend", fromMeta.Text);
        Assert.True(fromMeta.FromMeta);
        Assert.Contains("Cycle Rogue", fromMeta.Tooltip);
        Assert.Equal("No games with this deck yet", DeckHeader.Describe(null, null).Text);
        // Only ties or unknown results: no win rate of your own, so the meta speaks.
        Assert.Equal($"Meta {0.534.ToString("P0", CultureInfo.CurrentCulture)} · Legend", DeckHeader.Describe(new DeckAggregate("D", null, "ROGUE", 2, 0, 0, DateTime.Today), meta).Text);
    }

    /// <summary>Minimal deck code writer (format 2, one hero, every card listed with its count in the n-copies list).</summary>
    private static string Encode(int hero, IEnumerable<KeyValuePair<int, int>> cards)
    {
        var bytes = new List<byte>();
        void Varint(int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                bytes.Add((byte)(v | 0x80));
                v >>= 7;
            }

            bytes.Add((byte)v);
        }

        var list = cards.ToList();
        Varint(0);
        Varint(1);
        Varint(2);
        Varint(1);
        Varint(hero);
        var singles = list.Where(c => c.Value == 1).ToList();
        var doubles = list.Where(c => c.Value == 2).ToList();
        var many = list.Where(c => c.Value > 2).ToList();
        Varint(singles.Count);
        singles.ForEach(c => Varint(c.Key));
        Varint(doubles.Count);
        doubles.ForEach(c => Varint(c.Key));
        Varint(many.Count);
        many.ForEach(c =>
        {
            Varint(c.Key);
            Varint(c.Value);
        });
        return Convert.ToBase64String(bytes.ToArray());
    }
}
