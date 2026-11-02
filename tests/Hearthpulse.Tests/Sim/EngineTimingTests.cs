using System.Diagnostics;
using Hearthpulse.Core.Battlegrounds;
using Xunit.Abstractions;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim;

/// <summary>The simulator's speed on full boards with effects (wall clock, so alone).</summary>
[Collection(nameof(TimingCollection))]
public sealed class EngineTimingTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Timing")]
    public void Full_boards_with_effects_run_fast_enough()
    {
        var effects = Effects(
            typeof(EngineTests.SummonTwo), typeof(EngineTests.ExtraTrigger), typeof(EngineTests.RallyPlusTen), typeof(EngineTests.AvengeTwo),
            typeof(EngineTests.AuraPlusTwo), typeof(EngineTests.SummonWatcher), typeof(EngineTests.GainAttackWhenDamaged), typeof(EngineTests.Cleave));
        var ours = Side(
            Minion(4, 6, cardId: "DR"), Minion(2, 8, cardId: "TITUS"), Minion(3, 5, Keyword.Reborn, "DR"), Minion(5, 5, cardId: "AV"),
            Minion(2, 4, cardId: "AURA"), Minion(6, 6, Keyword.DivineShield, "DR"), Minion(3, 7, cardId: "WATCH"));
        var theirs = Side(
            Minion(5, 5, cardId: "RALLY"), Minion(4, 9, Keyword.Taunt, "HURT"), Minion(7, 3, Keyword.Reborn, "DR"), Minion(3, 3, cardId: "CLEAVE"),
            Minion(6, 6, Keyword.DivineShield), Minion(2, 10, Keyword.Taunt, "AV"), Minion(4, 4, Keyword.Poisonous));
        var keywordsOnly = (Side(ours.Minions.Select(m => m with { CardId = "" }).ToArray()), Side(theirs.Minions.Select(m => m with { CardId = "" }).ToArray()));

        CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 200, Effects = effects }); // warm-up
        var clock = Stopwatch.StartNew();
        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 4000, Effects = effects });
        var withEffects = clock.Elapsed;
        clock.Restart();
        CombatSimulator.Run(keywordsOnly.Item1, keywordsOnly.Item2, null, new SimOptions { Runs = 4000, Effects = effects });
        var plain = clock.Elapsed;

        output.WriteLine($"4000 runs 7v7: with effects {withEffects.TotalMilliseconds:0} ms, keywords only {plain.TotalMilliseconds:0} ms; odds {odds}");
        Assert.Equal(4000, odds.Runs);
        Assert.True(withEffects < TimeSpan.FromSeconds(5), $"too slow: {withEffects}");
    }
}
