using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Tests;

/// <summary>
/// An overlay update hands out the counter views of the last one that would come out the same (Counters.Build): the
/// result must be what building every counter gives, line by line, on synthetic games, the Battlegrounds fixture and
/// the kept logs; and an unchanged counter must be the very same view. Switches the language, so it runs alone.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class CounterReuseTests
{
    private static readonly CardDb Cards = Parse("enUS", "Anduin");

    private static CardDb Parse(string locale, string hero) => CardDb.Parse(locale, new MemoryStream(System.Text.Encoding.UTF8.GetBytes($$"""
        [
          {"id":"HERO_09","dbfId":813,"name":"{{hero}}","type":"HERO","cardClass":"PRIEST"},
          {"id":"HERO_01","dbfId":7,"name":"Garrosh","type":"HERO","cardClass":"WARRIOR"},
          {"id":"CORE_CS2_004","dbfId":120002,"name":"Power Word: Shield","type":"SPELL","cardClass":"PRIEST","cost":1,"spellSchool":"HOLY"},
          {"id":"BE_012","dbfId":120003,"name":"Dark Deacon","type":"MINION","cardClass":"PRIEST","cost":3},
          {"id":"CATA_480","dbfId":120004,"name":"Some Quest","type":"SPELL","cardClass":"PRIEST","cost":1,"mechanics":["QUEST"]},
          {"id":"CORE_EX1_284","dbfId":120007,"name":"Azure Drake","type":"MINION","cardClass":"NEUTRAL","cost":5,"races":["DRAGON"],"mechanics":["DEATHRATTLE"]},
          {"id":"TLC_515","dbfId":120008,"name":"Cultist Map","type":"SPELL","cardClass":"ROGUE","cost":1,"mechanics":["DISCOVER"]},
          {"id":"BE_013","dbfId":120001,"name":"C'Thun, Eye of the Empire","type":"MINION","cardClass":"NEUTRAL","cost":10}
        ]
        """)));

    private static readonly string[] Setup =
    [
        "DebugPrintPower() - CREATE_GAME",
        "DebugPrintPower() -     GameEntity EntityID=1",
        "DebugPrintPower() -         tag=CARDTYPE value=GAME",
        "DebugPrintPower() -         tag=TURN value=3",
        "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
        "DebugPrintPower() -         tag=CONTROLLER value=1",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=HERO_ENTITY value=20",
        "DebugPrintPower() -         tag=FIRST_PLAYER value=1",
        "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
        "DebugPrintPower() -         tag=CONTROLLER value=2",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=HERO_ENTITY value=21",
        "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_09",
        "DebugPrintPower() -     tag=ZONE value=PLAY",
        "DebugPrintPower() -     tag=CONTROLLER value=1",
        "DebugPrintPower() -     tag=CARDTYPE value=HERO",
        "DebugPrintPower() - FULL_ENTITY - Creating ID=21 CardID=HERO_01",
        "DebugPrintPower() -     tag=ZONE value=PLAY",
        "DebugPrintPower() -     tag=CONTROLLER value=2",
        "DebugPrintPower() -     tag=CARDTYPE value=HERO",
        "DebugPrintGame() - GameType=GT_RANKED",
        "DebugPrintGame() - FormatType=FT_STANDARD",
        "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
        "DebugPrintGame() - PlayerID=2, PlayerName=Rival#1234",
        "DebugPrintPower() - FULL_ENTITY - Creating ID=99 CardID=CORE_CS2_004",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        "DebugPrintPower() -     tag=CONTROLLER value=1",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
    ];

    private static string[] Card(int id, string cardId, string zone, int controller, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    private static string[] Play(int id, string cardId, int controller) =>
    [
        $"DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
        $"DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=PLAY ",
        "DebugPrintPower() - BLOCK_END",
    ];

    private static string Tag(string entity, string tag, object value) => $"DebugPrintPower() - TAG_CHANGE Entity={entity} tag={tag} value={value} ";

    private static string Entity(int id, string cardId, int controller) => $"[entityName=X id={id} zone=PLAY zonePos=1 cardId={cardId} player={controller}]";

    /// <summary>
    /// A game in steps: casts and plays on both sides, a quest that progresses, deaths, a hidden opponent card revealed,
    /// turns passing; the deck is set, the language and the card set change part way.
    /// </summary>
    private static IEnumerable<(string[] Lines, Action<ConstructedTracker>? Then)> Steps()
    {
        yield return ([.. Card(30, "CATA_480", "SECRET", 1, "SPELL", "QUEST_PROGRESS_TOTAL=3", "QUEST_PROGRESS=0")], null);
        yield return ([.. Card(31, "CORE_CS2_004", "HAND", 1, "SPELL", "SPELL_SCHOOL=5"), .. Play(31, "CORE_CS2_004", 1)], null);
        yield return ([Tag("[entityName=X id=30 zone=SECRET zonePos=0 cardId=CATA_480 player=1]", "QUEST_PROGRESS", 1)], null);
        yield return ([.. Card(32, "BE_012", "HAND", 1, "MINION"), .. Play(32, "BE_012", 1)], null);
        // Only the opponent changes here: an attack value on their hero.
        yield return ([Tag(Entity(21, "HERO_01", 2), "ATK", 3)], null);
        yield return ([.. Card(40, "", "HAND", 2, "INVALID")], null);
        yield return ([
            "DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=40 zone=HAND zonePos=1 cardId= player=2] CardID=CORE_EX1_284",
            "DebugPrintPower() -     tag=CARDTYPE value=MINION",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            .. Play(40, "CORE_EX1_284", 2),
        ], null);
        yield return ([Tag(Entity(40, "CORE_EX1_284", 2), "ZONE", "GRAVEYARD")], null);
        yield return ([Tag(Entity(32, "BE_012", 1), "ZONE", "GRAVEYARD")], tracker => tracker.Deck = new DeckDefinition(
            "Deck", 1, "", "FT_STANDARD", [813], new Dictionary<int, int> { [120001] = 1, [120007] = 2, [120008] = 1 }));
        yield return ([Tag("GameEntity", "TURN", 4)], null);
        yield return ([Tag("[entityName=X id=30 zone=SECRET zonePos=0 cardId=CATA_480 player=1]", "QUEST_PROGRESS", 2)], _ => Loc.Instance.SetLanguage(Loc.Russian));
        yield return ([.. Card(33, "TLC_515", "HAND", 1, "SPELL"), .. Play(33, "TLC_515", 1)], null);
        yield return ([Tag("GameEntity", "TURN", 5)], _ => Loc.Instance.SetLanguage(Loc.English));
        yield return ([Tag(Entity(21, "HERO_01", 2), "ATK", 0)], null);
    }

    /// <summary>
    /// On the headless Avalonia thread: switching the global language updates every live Avalonia text bound to it, and
    /// those belong to that thread (LocBindingTests).
    /// </summary>
    [Fact]
    public void Reused_counter_views_match_building_every_counter_line_by_line() => LocBindingTests.Headless.Run(LineByLine);

    private static void LineByLine()
    {
        try
        {
            var parser = new PowerParser(new DateOnly(2026, 10, 29));
            var tracker = new ConstructedTracker();
            parser.AddListener(tracker);
            parser.AddListener(tracker.Counters);
            foreach (var line in Setup)
            {
                parser.Feed("D 12:30:00.0000000 GameState." + line);
            }

            var cards = Cards;
            var built = 0;
            foreach (var (lines, then) in Steps())
            {
                foreach (var line in lines)
                {
                    parser.Feed("D 12:30:00.0000000 GameState." + line);
                    Compare(parser.Game!, tracker, cards);
                    built++;
                }

                then?.Invoke(tracker);
                if (built > 40 && ReferenceEquals(cards, Cards))
                {
                    // A new card set (the language's card names): nothing of the old one is handed out.
                    cards = Parse("ruRU", "Андуин");
                }

                Compare(parser.Game!, tracker, cards);
            }
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }
    }

    /// <summary>Builds the counters with reuse, then every one of them, and checks both read the same.</summary>
    private static void Compare(GameState game, ConstructedTracker tracker, CardDb cards)
    {
        var views = tracker.Counters.Views;
        var reused = Counters.Build(game, tracker.Counters, cards, tracker.Deck);
        views.Reuse = false;
        var fresh = Counters.Build(game, tracker.Counters, cards, tracker.Deck);
        views.Reuse = true;
        Assert.Equal(Json(fresh), Json(reused));
    }

    [Fact]
    public void An_unchanged_counter_is_the_same_view_and_a_changed_one_is_new()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 29));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        foreach (var line in Setup.Concat(Card(31, "CORE_CS2_004", "HAND", 1, "SPELL")).Concat(Play(31, "CORE_CS2_004", 1)))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        var before = Counters.Build(parser.Game!, tracker.Counters, Cards, null);
        // Only an opponent's hero changes: no counter reads differently, every view is handed out again.
        parser.Feed("D 12:30:00.0000000 GameState." + Tag(Entity(21, "HERO_01", 2), "ATK", 3));
        var opponentOnly = Counters.Build(parser.Game!, tracker.Counters, Cards, null);
        Assert.Equal(before.Count, opponentOnly.Count);
        Assert.All(before.Zip(opponentOnly), pair => Assert.Same(pair.First, pair.Second));

        // Another spell of yours: "cards played" is a new view, the opponent's side stays as it was.
        foreach (var line in Card(32, "CORE_CS2_004", "HAND", 1, "SPELL").Concat(Play(32, "CORE_CS2_004", 1)))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        var after = Counters.Build(parser.Game!, tracker.Counters, Cards, null);
        var played = (before.Single(c => c.Id == "cards-played" && !c.Opponent), after.Single(c => c.Id == "cards-played" && !c.Opponent));
        Assert.NotSame(played.Item1, played.Item2);
        Assert.Equal(("1", "2"), (played.Item1.Value, played.Item2.Value));
        Assert.Same(before.Single(c => c.Id == "cards-played" && c.Opponent), after.Single(c => c.Id == "cards-played" && c.Opponent));
    }

    [Fact]
    public void Snapshots_with_reused_counters_match_the_built_ones_on_the_battlegrounds_fixture() =>
        CompareSessions(BattlegroundsFixtureTests.Lines(), new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc), CardDb.Empty);

    [RealLogsFact]
    public void Snapshots_with_reused_counters_match_the_built_ones_on_the_kept_logs()
    {
        foreach (var dir in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*"))
        {
            var path = Path.Combine(dir, "Power.log");
            if (Core.Hearthstone.HearthstoneInstall.SessionStart(dir) is { } start && File.Exists(path))
            {
                CompareSessions(RealLogTests.SharedLines(path), start.ToUniversalTime(), Cards);
            }
        }
    }

    /// <summary>
    /// The same lines through two sessions, one building every counter, in batches of 100 ms of log time with a tick
    /// after each as the log watcher does: every published snapshot reads the same.
    /// </summary>
    private static void CompareSessions(IEnumerable<string> lines, DateTime start, CardDb cards)
    {
        var now = start;
        var db = Path.Combine(Path.GetTempPath(), $"hp-reuse-{Guid.NewGuid():N}");
        var memory = new HearthstoneMemory();
        GameSession Session(string name, List<string> published)
        {
            var session = new GameSession(new HistoryStore(db + name + ".db"), memory)
            {
                Clock = () => now,
                IsHearthstoneRunning = () => true,
                ReadsMemory = false,
                Trimmer = new Core.MemoryTrimmer(() => { }),
                Cards = cards,
            };
            session.SnapshotChanged += snapshot => published.Add(Json(WithoutCombatOdds(snapshot)));
            session.OnSessionStarted("Hearthstone_2026_10_29_00_00_00", start, attachedMidSession: false);
            return session;
        }

        var reusedSnapshots = new List<string>();
        var builtSnapshots = new List<string>();
        var reused = Session("a", reusedSnapshots);
        var built = Session("b", builtSnapshots);
        built.ReuseCounterViews = false;
        try
        {
            var batch = new List<string>();
            TimeSpan? batchStart = null;
            void Flush()
            {
                reused.OnLines("Power.log", batch, catchingUp: false);
                built.OnLines("Power.log", batch, catchingUp: false);
                batch = [];
                reused.OnTick();
                built.OnTick();
            }

            foreach (var line in lines)
            {
                if (line.Length > 18 && TimeSpan.TryParseExact(line.AsSpan(2, 16), @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture, out var at))
                {
                    if (batchStart is { } from && at - from >= TimeSpan.FromMilliseconds(100))
                    {
                        Flush();
                        now = start.Date + at;
                        batchStart = at;
                    }

                    batchStart ??= at;
                }

                batch.Add(line);
            }

            Flush();
            now = now.AddSeconds(1);
            Flush();
            Assert.NotEmpty(builtSnapshots);
            Assert.Equal(builtSnapshots.Count, reusedSnapshots.Count);
            for (var i = 0; i < builtSnapshots.Count; i++)
            {
                Assert.True(builtSnapshots[i] == reusedSnapshots[i], $"Snapshot {i} of {builtSnapshots.Count} differs");
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());

    /// <summary>
    /// Battlegrounds combat odds come from a simulation with a time budget and a refinement in the background: two
    /// sessions get them at different moments. They have nothing to do with the counters, so they are left out.
    /// </summary>
    private static LiveSnapshot WithoutCombatOdds(LiveSnapshot snapshot) => snapshot.Battlegrounds is { } bg
        ? snapshot with { Battlegrounds = bg with { CombatOdds = null, Combats = bg.Combats.Select(c => c with { Odds = null }).ToList() } }
        : snapshot;
}
