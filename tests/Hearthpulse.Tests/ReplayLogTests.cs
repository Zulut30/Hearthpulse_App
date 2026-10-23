using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit;
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

        Assert.False(recorder.HasGame);
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

                foreach (var record in LogBackfill.Parse(file, DateOnly.FromDateTime(start.Value), cards, replays: store))
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
}
