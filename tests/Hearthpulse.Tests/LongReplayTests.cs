using System.Diagnostics;
using System.Globalization;
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
                    var (attacker, target) = (board[side].Peek(), board[them].Dequeue());
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"A {attacker} {target}"));
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"V {target} DAMAGE 5"));
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"E {target} {(them == 0 ? "L" : "O")} GRAVEYARD 0 CS2_182 MINION ATK=4 HEALTH=5"));
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

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var clock = Stopwatch.StartNew();
        var page = MatchPage.Load(game, store, CardDb.Empty, null);
        var load = clock.Elapsed;
        var model = new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, "history.db")), new AppSettings(), TestDataFolder.App));
        model.Show(page);
        var held = GC.GetTotalMemory(forceFullCollection: true) - before;

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
}
