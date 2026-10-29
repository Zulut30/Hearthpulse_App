using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>Match replays recorded from real games: the kept Battlegrounds fixture and the local client's kept logs.</summary>
public sealed class ReplayLogTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-replay-logs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Battlegrounds_fixture_keeps_rounds_shop_and_lobby()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker();
        var recorder = new ReplayRecorder();
        parser.AddListener(tracker);
        parser.AddListener(recorder);
        foreach (var line in BattlegroundsFixtureTests.Lines())
        {
            parser.Feed(line);
        }

        parser.Flush();
        var data = tracker.ReplayData(parser.Game!);
        foreach (var round in data.Rounds)
        {
            output.WriteLine($"turn {round.Turn} tier {round.Tier} hp {round.Health} board {round.Board.Count} vs {round.OpponentHero} {round.OpponentBoard.Count} shop {string.Join(",", round.Shop)} {round.Result}");
        }

        // Format 4 records the game step by step as well (it used to keep only the rounds).
        Assert.True(recorder.HasGame);
        Assert.True(recorder.IsBattlegrounds);
        var first = data.Rounds[0];
        Assert.Equal(1, first.Turn);
        Assert.True(first.Shop.Count >= 3);
        Assert.Equal("Tie", first.Result);
        Assert.NotNull(first.OpponentHero);
        Assert.Equal(8, data.Lobby.Count);
        Assert.Single(data.Lobby, h => h.Local);
    }

    [RealLogsFact]
    public void Kept_logs_record_replays_with_nothing_hidden()
    {
        var store = new ReplayStore(_dir);
        var cards = CardDb.Empty;
        var games = 0;
        foreach (var dir in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*"))
        {
            var start = Core.Hearthstone.HearthstoneInstall.SessionStart(dir);
            foreach (var name in new[] { "Power_old.log", "Power.log" })
            {
                var file = Path.Combine(dir, name);
                if (start is null || !File.Exists(file))
                {
                    continue;
                }

                foreach (var record in LogBackfill.Parse(file, start.Value, cards, replays: store))
                {
                    var text = store.Load(record.Replay);
                    Assert.NotNull(text);
                    var (header, body) = ReplayFile.Read(text!)!.Value;
                    output.WriteLine($"{record.StartedAt:g} {record.Mode,-13} {record.Result,-8} {header.Kind,-13} {body.Count,6} lines {new FileInfo(Path.Combine(_dir, record.Replay!)).Length / 1024,5} KB{(header.Truncated ? " truncated" : "")}");
                    if (header.Kind == ReplayFile.Constructed)
                    {
                        ReplayChecks.AssertNothingHidden(body);
                        Assert.Contains(body, l => l.StartsWith("T ", StringComparison.Ordinal));
                        if (record.Turns >= 3)
                        {
                            Assert.Contains(body, l => l.StartsWith("P ", StringComparison.Ordinal));
                        }

                        CheckModel(record, MatchReplay.Parse(text!)!);
                    }
                    else
                    {
                        Assert.NotEmpty(ReplayFile.ReadBattlegrounds(body)!.Rounds);
                    }

                    games++;
                }
            }
        }

        output.WriteLine($"{games} replays");
    }

    /// <summary>The folded replay of a real game: card backs stay card backs at every step, turns run in order, the end matches the row.</summary>
    private void CheckModel(GameRecord record, MatchReplay replay)
    {
        Assert.NotEmpty(replay.Frames);
        foreach (var frame in replay.Frames)
        {
            Assert.All(frame.Opponent.Hand, c => Assert.True(c.IsCardBack));
            Assert.All(frame.Opponent.Secrets.Where(s => s.Type == "-"), c => Assert.True(c.IsCardBack));
            Assert.All(frame.Local.Hand, c => Assert.False(c.IsCardBack));
            Assert.InRange(frame.Local.DeckCount, 0, 80);
            Assert.InRange(frame.Local.Hand.Count + frame.Opponent.Hand.Count, 0, 20);
        }

        Assert.True(replay.Turns.Select(t => t.Turn).SequenceEqual(replay.Turns.Select(t => t.Turn).Order()));
        var expected = record.Result switch { "Win" => "WON", "Loss" => "LOST", "Tie" => "TIED", _ => null };
        if (expected is not null)
        {
            Assert.True(replay.Result == expected || (expected == "LOST" && replay.Result == "CONCEDED"), $"{record.Result} vs {replay.Result}");
        }

        if (replay.Mulligan is { } mulligan)
        {
            Assert.InRange(mulligan.Offered.Count, 3, 5);
            Assert.Equal(mulligan.Replaced.Count, mulligan.Received.Count);
        }

        // The post-game part: every card the opponent played was a card back in its hand the log then named.
        Assert.True(replay.HasPostGame);
        var revealed = replay.Reveals.Select(r => r.CardId).ToHashSet(StringComparer.Ordinal);
        Assert.All(replay.OpponentPlayed, c => Assert.Contains(c.CardId, revealed));
        var backs = replay.Frames.SelectMany(f => f.Opponent.Hand).Where(c => c.IsCardBack).ToList();
        output.WriteLine($"    reveals {replay.Reveals.Count} ({string.Join(", ", replay.Reveals.GroupBy(r => r.How).Select(g => $"{g.Key} {g.Count()}"))}), "
                         + $"hand backs named {backs.Count(c => c.Reveal is not null)}/{backs.Count}, their mulligan "
                         + (replay.OpponentMulligan is { } m ? $"kept {m.Kept.Count(k => k is not null)}/{m.Kept.Count} replaced {m.Replaced.Count(k => k is not null)}/{m.Replaced.Count} received {m.Received.Count}" : "-"));
        if (replay.OpponentMulligan is { } theirs)
        {
            Assert.InRange(theirs.Kept.Count + theirs.Replaced.Count, 3, 4);
            Assert.Equal(theirs.Replaced.Count, theirs.Received.Count);
        }

        var analysis = MatchAnalysis.Build(record, replay, CardDb.Empty, null);
        Assert.All(analysis.LocalMana.Concat(analysis.OpponentMana), m => Assert.InRange(m, 0, 1));
        Assert.Equal(replay.Turns.Count(t => t.Turn > 0), analysis.TurnRows.Count);
        Assert.All(analysis.LocalHealth, h => Assert.InRange(h ?? 0, -30, 200));

        var last = replay.Frames[^1];
        output.WriteLine($"    {replay.Frames.Count} frames, {replay.Turns.Count} turns, {replay.LocalPlayed.Sum(c => c.Count)} / {replay.OpponentPlayed.Sum(c => c.Count)} played, "
                         + $"{replay.Frames.Count(f => f.Action.Kind == ReplayActionKind.Attack)} attacks, {replay.Choices.Count} choices, "
                         + $"mulligan {replay.Mulligan?.Kept.Count}/{replay.Mulligan?.Offered.Count} kept, mana {(analysis.LocalMana.Count > 0 ? analysis.LocalMana.Average() : 0):P0}, key {string.Join("; ", analysis.KeyCards.Take(2).Select(k => k.CardId + " " + k.Reason))}, end hp {last.Local.Health}+{last.Local.Armor} vs {last.Opponent.Health}+{last.Opponent.Armor}");
    }
}
