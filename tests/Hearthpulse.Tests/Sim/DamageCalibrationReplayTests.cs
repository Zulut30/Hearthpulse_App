using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests.Sim;

/// <summary>
/// Replays real Battlegrounds logs and prints each combat's predicted damage next to what the log showed, plus the
/// calibration numbers the Battlegrounds page shows. The repository fixture always runs; the full 2022 game
/// (<see cref="FullGameLog"/>) and any BG game in <see cref="RealLogTests.LogRoot"/> only on a machine that has them
/// (the report is printed, not asserted: the numbers depend on which games the machine kept).
/// </summary>
public sealed class DamageCalibrationReplayTests(ITestOutputHelper output)
{
    private const string FullGameLog = @"D:\BGSim\output\h10-source\139963.power.log";

    [Fact]
    public void Fixture_combat_is_predicted_and_checked()
    {
        var combats = Replay(BattlegroundsFixtureTests.Lines(), new DateOnly(2026, 10, 23), "fixture");

        var combat = Assert.Single(combats);
        Assert.NotNull(combat.Odds);
        // A tie has no damage to check, but the result counts.
        Assert.Equal(1, BattlegroundsStats.SimAccuracy([Record(combats)]).Fights);
    }

    [Fact]
    public void Real_logs_report_calibration()
    {
        var logs = new List<string>();
        if (File.Exists(FullGameLog))
        {
            logs.Add(FullGameLog);
        }

        if (Directory.Exists(RealLogTests.LogRoot))
        {
            logs.AddRange(Directory.EnumerateFiles(RealLogTests.LogRoot, "Power*.log", SearchOption.AllDirectories)
                .Where(f => RealLogTests.SharedLines(f).Any(l => l.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal))));
        }

        if (logs.Count == 0)
        {
            output.WriteLine("No Battlegrounds logs on this machine.");
            return;
        }

        var records = new List<GameRecord>();
        foreach (var log in logs)
        {
            var combats = Replay(RealLogTests.SharedLines(log), DateOnly.FromDateTime(File.GetLastWriteTime(log)), log);
            Assert.All(combats.Where(c => c.Odds is not null), c => Assert.InRange(c.Odds!.Win + c.Odds.Tie + c.Odds.Loss, 0.999, 1.001));
            records.Add(Record(combats));
        }

        var accuracy = BattlegroundsStats.SimAccuracy(records);
        // Logs from before BACON_CURRENT_COMBAT_PLAYER_ID (the 2022 one) record no combats at all: nothing to check there.
        output.WriteLine($"Accuracy: {accuracy.Fights} fights, result {accuracy.ResultRate:P0}, damage in range {accuracy.DamageHits}/{accuracy.DamageFights} = {accuracy.DamageRate:P0}");
    }

    private IReadOnlyList<CombatRecord> Replay(IEnumerable<string> lines, DateOnly date, string name)
    {
        var parser = new PowerParser(date);
        var tracker = new BattlegroundsTracker { Cards = CardDb.Empty };
        parser.AddListener(tracker);
        IReadOnlyList<CombatRecord> combats = [];
        var count = 0;
        foreach (var line in lines)
        {
            parser.Feed(line);
            // Views now and then, as the overlay builds them: that is when the background simulation's result is taken.
            if (++count % 200 == 0 && line.Contains(" TAG_CHANGE ", StringComparison.Ordinal) && parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                parser.Flush();
                tracker.BuildView(game);
            }

            // A game's combats are cleared when the next one starts: keep the longest list seen.
            if (tracker.Combats.Count >= combats.Count && tracker.Combats.Count > 0)
            {
                combats = tracker.Combats.ToList();
            }
        }

        output.WriteLine($"{name}: {combats.Count} combats");
        foreach (var c in combats)
        {
            var o = c.Odds;
            output.WriteLine(o is null
                ? $"  T{c.Turn} {c.Result} taken={c.DamageTaken} dealt={c.DamageDealt}: no prediction"
                : $"  T{c.Turn} {c.Result} taken={c.DamageTaken} dealt={c.DamageDealt} | win {o.Win:P0} tie {o.Tie:P0} loss {o.Loss:P0}"
                  + $" | taken {o.DamageTaken?.Low}-{o.DamageTaken?.High} (max {o.DamageTaken?.Max}) dealt {o.DamageDealt?.Low}-{o.DamageDealt?.High} (max {o.DamageDealt?.Max})"
                  + $" | cap {o.DamageCap} lethal {o.LethalTaken:P1} kill {o.LethalDealt:P1} runs {o.Runs} unsim {o.Unsimulated}");
        }

        return combats;
    }

    private static GameRecord Record(IReadOnlyList<CombatRecord> combats) => new()
    {
        Key = Guid.NewGuid().ToString(),
        Mode = "Battlegrounds",
        CombatResults = System.Text.Json.JsonSerializer.Serialize(combats.Select(c => new
            { turn = c.Turn, result = c.Result, opponent = c.OpponentHeroCardId, taken = c.DamageTaken, dealt = c.DamageDealt })),
        CombatOdds = GameRecords.CombatOddsJson(combats),
    };
}
