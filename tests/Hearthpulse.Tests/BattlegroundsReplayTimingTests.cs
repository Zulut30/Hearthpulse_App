using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>The recorder on a real Battlegrounds game: size, content and the parser's speed with it (wall clock, so alone).</summary>
[Collection(nameof(TimingCollection))]
public sealed class BattlegroundsReplayTimingTests(ITestOutputHelper output)
{
    [RealLogsFact]
    [Trait("Category", "Timing")]
    public void The_28_10_game_is_recorded_whole_under_a_megabyte_and_without_slowing_the_parser()
    {
        if (!File.Exists(BattlegroundsReplayTests.RealLog))
        {
            output.WriteLine($"skipped: {BattlegroundsReplayTests.RealLog} is not here");
            return;
        }

        // The Battlegrounds game only: from its CREATE_GAME on (the log holds two Constructed games before it).
        var all = File.ReadAllLines(BattlegroundsReplayTests.RealLog);
        var type = Array.FindIndex(all, l => l.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal));
        var lines = all[Array.FindLastIndex(all, type, l => l.Contains("GameState.DebugPrintPower() - CREATE_GAME", StringComparison.Ordinal))..];
        // Three alternated runs of each, the median of each: the tracker's background simulations and the GC make a
        // single run noisy. A first pair warms the JIT up.
        Time(lines, record: false, out _);
        Time(lines, record: true, out _);
        var (plain, recorded) = (new List<TimeSpan>(), new List<TimeSpan>());
        (ReplayRecorder Recorder, BattlegroundsTracker Tracker, GameState? Game) end = default;
        for (var run = 0; run < 3; run++)
        {
            plain.Add(Time(lines, record: false, out _));
            recorded.Add(Time(lines, record: true, out end));
        }

        var without = plain.Order().ElementAt(1);
        var with = recorded.Order().ElementAt(1);

        var text = end.Recorder!.Battlegrounds.Text(new ReplayHeader(), end.Tracker!.ReplayData(end.Game!));
        var frames = BattlegroundsMatchReplay.Parse(text)!.Frames.Count;
        var (header, body) = ReplayFile.Read(text)!.Value;
        var gzip = Gzip(text);
        output.WriteLine($"events {header.Events}, lines {body.Count}, frames {frames}, text {Encoding.UTF8.GetByteCount(text)} B, gzip {gzip} B, truncated {header.Truncated}");
        output.WriteLine($"parse {without.TotalMilliseconds:F0} ms, with the recorder {with.TotalMilliseconds:F0} ms (medians of "
                         + $"{string.Join("/", plain.Select(t => t.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))} and "
                         + $"{string.Join("/", recorded.Select(t => t.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}), x{with / without:F2}");

        Assert.False(header.Truncated);
        Assert.InRange(header.Events, 1, BattlegroundsReplayRecorder.MaxEvents - 1);
        Assert.True(gzip <= 1024 * 1024, $"{gzip} B");
        Assert.Equal(12, body.Count(l => l.StartsWith("F ", StringComparison.Ordinal)));
        Assert.Equal(12, body.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.Equal("R 4", body[header.Events - 1]);
        // 47 plays from hand and 17 activated powers of BG36_311 / BG36_099(_G) on the table.
        Assert.Equal((47, 17), (body.Count(l => l.StartsWith("M PLAY ", StringComparison.Ordinal)), body.Count(l => l.StartsWith("M USE ", StringComparison.Ordinal))));
        BattlegroundsReplayTests.AssertSeenOnly(body.Take(header.Events).ToList());
        // The hero was rerolled before the pick (BG28_HERO_400_SKIN_C -> BG36_HERO_000): the first step offers it.
        BattlegroundsReplayTests.AssertPicksOffered(body);
        var steps = BattlegroundsMatchReplay.Parse(text)!.Frames;
        // The last step's leaderboard has our final place, set by the game after PLAYSTATE.
        Assert.Equal(4, steps[^1].Leaderboard.Single(l => l.Local).Place);
        Assert.Equal(["BG36_HERO_000"], steps[0].Choice!.Picked);
        Assert.Contains("BG36_HERO_000", steps[0].Choice!.Options);
        Assert.True(with / without <= 1.25, $"x{with / without:F2}");
    }

    /// <summary>One parse of the lines with the trackers of a log import, with or without the recorder.</summary>
    private static TimeSpan Time(string[] lines, bool record, out (ReplayRecorder Recorder, BattlegroundsTracker Tracker, GameState? Game) end)
    {
        var parser = new PowerParser(new DateTime(2026, 10, 28, 0, 19, 35));
        var tracker = new BattlegroundsTracker { Cards = CardDb.Empty };
        var recorder = new ReplayRecorder();
        parser.AddListener(new ConstructedTracker());
        parser.AddListener(tracker);
        parser.AddListener(new GameFactsTracker());
        if (record)
        {
            parser.AddListener(recorder);
        }

        var watch = Stopwatch.StartNew();
        foreach (var line in lines)
        {
            parser.Feed(line);
        }

        parser.AbandonCurrentGame();
        var elapsed = watch.Elapsed;
        end = (recorder, tracker, parser.Game);
        return elapsed;
    }

    private static long Gzip(string text)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return memory.Length;
    }
}
