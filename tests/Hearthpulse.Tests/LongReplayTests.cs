using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// A replay at the recorder's cap (plan R5): <see cref="ReplayRecorder.MaxEvents"/> events of a long Constructed game.
/// The page opens it, keeps it in bounded memory and steps through it without stalls.
/// </summary>
[CollectionDefinition(nameof(LongReplay), DisableParallelization = true)]
public sealed class LongReplay;

/// <summary>Timed: runs alone, not beside other tests on a loaded machine.</summary>
[Collection(nameof(LongReplay))]
public sealed class LongReplayTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = TestDataFolder.Scratch("hp-long-replay");

    public void Dispose() => TestDataFolder.TryDelete(_folder);

    /// <summary>
    /// A game of <paramref name="events"/> events: turns of ten resources, each drawing, playing, attacking with and
    /// losing minions, so the board and hand stay full (seven minions, up to ten cards) the whole way.
    /// </summary>
    internal static List<string> Events(int events)
    {
        var lines = new List<string>(events + 16)
        {
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30", "E 6 L PLAY 0 HERO_08bp HERO_POWER",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
        };
        var id = 100;
        var board = new[] { new Queue<int>(), new Queue<int>() };
        var hand = new[] { new Queue<int>(), new Queue<int>() };
        for (var turn = 1; lines.Count < events - 12; turn++)
        {
            var side = turn % 2;
            var who = side == 0 ? "L" : "O";
            var them = 1 - side;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"T {turn} {who} {turn * 40}"));
            lines.Add($"V {who} RESOURCES 10");
            for (var play = 0; play < 4 && lines.Count < events - 12; play++)
            {
                var drawn = id++;
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"E {drawn} {who} HAND {hand[side].Count + 1} {(side == 0 ? "CS2_120" : "-")} MINION ATK=2 HEALTH=3"));
                hand[side].Enqueue(drawn);
                if (hand[side].Count > 9)
                {
                    var minion = hand[side].Dequeue();
                    lines.Add("B");
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"E {minion} {who} PLAY {board[side].Count + 1} CS2_182 MINION ATK=4 HEALTH=5"));
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"P {minion} -"));
                    board[side].Enqueue(minion);
                }

                if (board[side].Count > 0 && board[them].Count > 0)
                {
                    // A hit; it kills only on a full board, so both tables stay at four to seven minions.
                    var (attacker, target) = (board[side].Peek(), board[them].Peek());
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"A {attacker} {target}"));
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"V {target} DAMAGE {play + 1}"));
                    if (board[them].Count > 4)
                    {
                        board[them].Dequeue();
                        lines.Add(string.Create(CultureInfo.InvariantCulture, $"E {target} {(them == 0 ? "L" : "O")} GRAVEYARD 0 CS2_182 MINION ATK=4 HEALTH=5"));
                    }
                }

                while (board[side].Count > 7)
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"X {board[side].Dequeue()}"));
                }
            }
        }

        lines.Add("R WON");
        return lines;
    }

    [Fact]
    public void A_replay_at_the_event_cap_opens_in_bounded_memory_and_steps_without_stalls()
    {
        var events = Events(ReplayRecorder.MaxEvents);
        Assert.InRange(events.Count, ReplayRecorder.MaxEvents - 20, ReplayRecorder.MaxEvents);
        var text = ReplayFile.Write(new ReplayHeader { Truncated = true, Events = events.Count }, events);
        var store = new ReplayStore(Path.Combine(_folder, "replays"));
        var game = MatchTimelineTests.Game(null) with { Turns = 0 };
        game = game with { Replay = store.Save(game.Key, text) };

        WarmUp(store);
        var before = LiveBytes();
        var clock = Stopwatch.StartNew();
        var page = MatchPage.Load(game, store, CardDb.Empty, null);
        var load = clock.Elapsed;
        var pageHeld = LiveBytes() - before;
        var model = new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, "history.db")), new AppSettings(), TestDataFolder.App));
        model.Show(page);
        var held = LiveBytes() - before;
        output.WriteLine($"page {pageHeld / 1024 / 1024} MB, with the match view {held / 1024 / 1024} MB");

        // Every step one by one (the playback, the arrows), then jumps across the game (the timeline, the "Turn" list).
        var steps = new List<double>(page.FrameCount);
        for (var frame = 0; frame <= model.FrameMax; frame += Math.Max(1, model.FrameMax / 2000))
        {
            clock.Restart();
            model.GoFrame.Execute(frame);
            steps.Add(clock.Elapsed.TotalMilliseconds);
        }

        var random = new Random(5);
        for (var i = 0; i < 500; i++)
        {
            clock.Restart();
            model.GoFrame.Execute(random.Next(model.FrameMax + 1));
            steps.Add(clock.Elapsed.TotalMilliseconds);
        }

        steps.Sort();
        var median = steps[steps.Count / 2];
        var worst = steps[^1];
        output.WriteLine($"{events.Count} events, {page.FrameCount} steps; load {load.TotalMilliseconds:F0} ms, held {held / 1024 / 1024} MB; step median {median:F2} ms, p99 {steps[steps.Count * 99 / 100]:F2} ms, worst {worst:F1} ms");

        Assert.True(page.FrameCount > 5000, $"{page.FrameCount} steps");
        // Measured 0.7 s and 25 MB on the owner's machine (they were 10.7 s and 84 MB before the frames walked only the
        // cards on the table and shared the unchanged ones); the limits leave room for a slow runner.
        Assert.True(load < TimeSpan.FromSeconds(4), $"load {load}");
        Assert.True(held < 64L * 1024 * 1024, $"held {held / 1024 / 1024} MB");
        Assert.True(median < 5, $"step median {median} ms");
        Assert.True(steps[steps.Count * 99 / 100] < 50, $"step p99 {steps[steps.Count * 99 / 100]} ms");
        GC.KeepAlive(model);
    }

    /// <summary>
    /// The live managed bytes after a blocking, compacting collection of every generation and the large object heap
    /// (the long replay's strings live there), finalizers run in between: what is held, not what waits to be swept.
    /// </summary>
    private static long LiveBytes()
    {
        for (var i = 0; i < 2; i++)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: false);
    }

    /// <summary>
    /// A short game opened once in a match view before measuring, so what the first match view of the process builds for
    /// good (static tables, caches) is not counted as the long replay's.
    /// </summary>
    private void WarmUp(ReplayStore store)
    {
        var warm = MatchTimelineTests.Game(null) with { Key = "20261105110000|warm-up|GT_RANKED", Turns = 0 };
        warm = warm with { Replay = store.Save(warm.Key, ReplayFile.Write(new ReplayHeader(), Events(200))) };
        var model = new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, "warm.db")), new AppSettings(), TestDataFolder.App));
        model.Show(MatchPage.Load(warm, store, CardDb.Empty, null));
        model.GoFrame.Execute(model.FrameMax);
    }

    /// <summary>
    /// A Battlegrounds file of <paramref name="events"/> events: turns of a buy, a play and a won combat against four
    /// minions, our board kept at seven (the shape of Game5th, MatchTableInPlaceTests).
    /// </summary>
    internal static string BattlegroundsText(int events)
    {
        var lines = new List<string>(events + 8) { "E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30", "E 2 L PLAY 0 TB_BaconShop_HP_050 HERO_POWER" };
        var board = new Queue<int>();
        var id = 100;
        string Line(FormattableString line) => FormattableString.Invariant(line);
        for (var turn = 1; lines.Count < events - 40; turn++)
        {
            var (shop, bought, played) = (id++, id++, id++);
            lines.Add(Line($"T {turn} 0"));
            lines.Add(Line($"E {shop} B PLAY 1 BGS_004 MINION ATK=1 HEALTH=1 TECH_LEVEL=1"));
            lines.Add(Line($"E {bought} B PLAY 2 BGS_039 MINION ATK=2 HEALTH=1 TECH_LEVEL=1"));
            lines.Add("V L RESOURCES 10");
            lines.Add(Line($"M BUY {bought}"));
            lines.Add("V L RESOURCES_USED 3");
            lines.Add(Line($"E {bought} L HAND 0 BGS_039 MINION ATK=2 HEALTH=1 TECH_LEVEL=1"));
            lines.Add(Line($"E {played} L PLAY {board.Count + 1} BG26_801 MINION ATK=4 HEALTH=6"));
            lines.Add(Line($"X {bought}"));
            board.Enqueue(played);
            while (board.Count > 7)
            {
                lines.Add(Line($"X {board.Dequeue()}"));
            }

            lines.Add(Line($"F {turn} 0"));
            lines.Add(Line($"X {shop}"));
            var hero = id++;
            lines.Add(Line($"E {hero} O PLAY 0 BG20_HERO_102 HERO HEALTH=30"));
            var theirs = Enumerable.Range(0, 4).Select(_ => id++).ToList();
            for (var i = 0; i < theirs.Count; i++)
            {
                lines.Add(Line($"E {theirs[i]} O PLAY {i + 1} BGS_119 MINION ATK=1 HEALTH=2"));
            }

            foreach (var target in theirs)
            {
                lines.Add(Line($"M ATTACK {board.Peek()} {target}"));
                lines.Add(Line($"V {target} DAMAGE 2"));
                lines.Add("M DEATHS -");
                lines.Add(Line($"Y {target}"));
            }

            lines.Add("f WIN 0 5");
            lines.Add(Line($"X {hero}"));
        }

        return "HPR4\n" + Line($"{{\"kind\":\"battlegrounds\",\"truncated\":true,\"events\":{lines.Count}}}") + "\n"
            + string.Join('\n', lines) + "\nR 1\nG {\"rounds\":[],\"lobby\":[]}\n";
    }

    /// <summary>
    /// A Battlegrounds replay at its recorder's cap (<see cref="BattlegroundsReplayRecorder.MaxEvents"/>, review m12):
    /// measured and held to the same kind of limits as the Constructed one.
    /// </summary>
    [Fact]
    public void A_battlegrounds_replay_at_the_event_cap_opens_and_steps_within_limits()
    {
        var text = BattlegroundsText(BattlegroundsReplayRecorder.MaxEvents);
        var store = new ReplayStore(Path.Combine(_folder, "bg-replays"));
        var game = MatchTimelineTests.Game(null) with { Key = "20261105120000|long-bg|GT_BATTLEGROUNDS", Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 1 };
        game = game with { Replay = store.Save(game.Key, text) };

        WarmUp(store);
        var before = LiveBytes();
        var clock = Stopwatch.StartNew();
        var page = MatchPage.Load(game, store, CardDb.Empty, null);
        var load = clock.Elapsed;
        var model = new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, "bg-history.db")), new AppSettings(), TestDataFolder.App));
        model.Show(page);
        var held = LiveBytes() - before;
        Assert.True(model.ShowBattlegroundsTable);

        var steps = new List<double>();
        var random = new Random(7);
        for (var i = 0; i < 1500; i++)
        {
            clock.Restart();
            model.GoFrame.Execute(i < 1000 ? i : random.Next(model.FrameMax + 1));
            steps.Add(clock.Elapsed.TotalMilliseconds);
        }

        steps.Sort();
        var (median, p99) = (steps[steps.Count / 2], steps[steps.Count * 99 / 100]);
        output.WriteLine($"battlegrounds: {page.FrameCount} steps; load {load.TotalMilliseconds:F0} ms, held {held / 1024 / 1024} MB; step median {median:F2} ms, p99 {p99:F2} ms, worst {steps[^1]:F1} ms");

        Assert.True(page.FrameCount > 10_000, $"{page.FrameCount} steps");
        Assert.True(load < TimeSpan.FromSeconds(8), $"load {load}");
        Assert.True(held < 400L * 1024 * 1024, $"held {held / 1024 / 1024} MB");
        Assert.True(median < 10, $"step median {median} ms");
        Assert.True(p99 < 100, $"step p99 {p99} ms");
        GC.KeepAlive(model);
    }

    /// <summary>
    /// The frames are the same with the card index and the shared cards as with the walk over every card that built each
    /// card anew (review m6): every frame of a long game (graveyard, X lines and freed slots) and of the golden game.
    /// </summary>
    [Fact]
    public void Frames_are_the_same_with_the_card_index_as_with_the_walk_over_every_card()
    {
        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay", "Golden", "constructed-win.hpr"))
            .Replace("@STARTED@", "2026-11-05T12:30:00Z", StringComparison.Ordinal);
        var events = Events(15_000);
        foreach (var text in new[] { golden, ReplayFile.Write(new ReplayHeader { Events = events.Count }, events) })
        {
            var fast = MatchReplay.Parse(text, reference: false)!;
            var walked = MatchReplay.Parse(text, reference: true)!;
            Assert.Equal(walked.Frames.Count, fast.Frames.Count);
            Assert.Equal(walked.Frames.Select(Describe), fast.Frames.Select(Describe));
        }

        // The long game's middle step has minions and cards in hand on both sides: the comparison covers full tables.
        var frames = MatchReplay.Parse(ReplayFile.Write(new ReplayHeader { Events = events.Count }, events))!.Frames;
        var middle = frames[frames.Count / 2];
        Assert.True(middle.Local.Minions.Count > 3 && middle.Local.Hand.Count > 3 && middle.Opponent.Minions.Count > 3 && middle.Opponent.Hand.Count > 3);
    }

    private static string Describe(ReplayFrame frame) =>
        $"{frame.Index} {frame.Turn} {frame.Active} {frame.Mulligan} {frame.Action with { Deaths = [], Picked = [] }} "
        + $"[{string.Join(",", frame.Action.Deaths)}] [{string.Join(",", frame.Action.Picked)}] L {Describe(frame.Local)} O {Describe(frame.Opponent)}";

    private static string Describe(ReplayBoard board) =>
        $"{Describe(board.Hero)} {Describe(board.HeroPower)} {Describe(board.Weapon)} m[{string.Join(";", board.Minions.Select(Describe))}] "
        + $"h[{string.Join(";", board.Hand.Select(Describe))}] s[{string.Join(";", board.Secrets.Select(Describe))}] {board.DeckCount} {board.Mana} {board.ManaUsed} {board.OverloadLocked}";

    private static string Describe(ReplayCard? card) =>
        card is null ? "-" : $"{card with { Keywords = [] }} k[{string.Join(",", card.Keywords)}]";
}
