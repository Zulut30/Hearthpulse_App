using System.Text.Json.Nodes;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.GameStats;
using Hearthpulse.Core.Replay;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>Game records built from the kept Hearthstone logs: every finished game of a sent mode, nothing hidden.</summary>
public sealed class GameStatsRealLogTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hp-gamestats-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [RealLogsFact]
    public void Kept_logs_give_records_within_the_pool_limits_and_with_nothing_hidden()
    {
        var store = new ReplayStore(_dir);
        var records = 0;
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

                foreach (var game in LogBackfill.Parse(file, start.Value, CardDb.Empty, replays: store))
                {
                    var text = store.Load(game.Replay);
                    if (GameStatsRecord.Build(game, new string('a', 32), text, null, CardDb.Empty, "0.18.0") is not { } record)
                    {
                        Assert.True(GameStatsRecord.ModeOf(game.Mode) is null || game.Result is not ("Win" or "Loss" or "Tie") || game.IsBattlegrounds,
                            $"{game.Mode} {game.Result} gave no record");
                        continue;
                    }

                    records++;
                    var json = record.ToJsonString();
                    output.WriteLine($"{game.StartedAt:g} {game.Mode,-13} {game.Result,-5} {json.Length / 1024.0,5:F1} KB");
                    if (Environment.GetEnvironmentVariable("HP_PRINT_RECORDS") == "1")
                    {
                        output.WriteLine(json);
                    }

                    Assert.True(json.Length < 24 * 1024, $"{json.Length} bytes");
                    foreach (var who in new[] { game.PlayerName, game.OpponentName })
                    {
                        if (who is { Length: > 2 })
                        {
                            Assert.DoesNotContain(who, json, StringComparison.Ordinal);
                        }
                    }

                    if (text is null || game.IsBattlegrounds)
                    {
                        continue;
                    }

                    // An opponent card in the record was shown during the game: an event line names it, not only the post-game part.
                    var body = ReplayFile.Read(text)!.Value.Body;
                    var shown = body.Where(l => l.StartsWith("E ", StringComparison.Ordinal)).Select(l => l.Split(' '))
                        .Where(t => t.Length > 5 && t[2] == "O" && t[5] != "-").Select(t => t[5]).ToHashSet(StringComparer.Ordinal);
                    foreach (var play in record["oplays"]!.AsArray())
                    {
                        Assert.Contains((string)play![1]!, shown);
                    }

                    Assert.All(record["plays"]!.AsArray(), p => Assert.InRange((int)p![0]!, 0, GameStatsRecord.MaxTurn));
                }
            }
        }

        output.WriteLine($"{records} records");
        Assert.True(records > 0);
    }
}
