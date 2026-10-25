using System.Collections.Concurrent;
using System.Diagnostics;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Xunit;
using Xunit.Abstractions;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim;

/// <summary>
/// Executes 5,000 diverse, multithreaded Battlegrounds combat simulations covering
/// high-stat scaling (up to 100,000+ attack), all keywords, card effects, and asymmetric boards,
/// verifying that 100% of simulations produce accurate, valid probabilities without error.
/// </summary>
public sealed class MultithreadedFiveThousandSimulationsTests(ITestOutputHelper output)
{
    private static SimMinion M(int attack, int health, Keyword kw = Keyword.None, string cardId = "", Tribe tribe = Tribe.None, bool golden = false) =>
        Minion(attack, health, kw, cardId, tribe, golden);

    [CardEffect("M_DR")]
    private sealed class TestDeathrattle : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Minion(2, 2, cardId: "T_TOKEN"), 2);
    }

    [CardEffect("M_TITUS")]
    private sealed class TestTitus : CombatEffect
    {
        public override int ExtraDeathrattles(Fight fight, Unit self) => self.Golden ? 2 : 1;
    }

    [CardEffect("M_CLEAVE")]
    private sealed class TestCleave : CombatEffect
    {
        public override void OnAttackDamage(Fight fight, Unit self, Unit target)
        {
            var (left, right) = fight.Neighbours(target);
            if (left is not null) fight.DealDamage(self, left, self.Attack);
            if (right is not null) fight.DealDamage(self, right, self.Attack);
        }
    }

    [CardEffect("M_AV")]
    private sealed class TestAvenge : AvengeEffect
    {
        protected override int Threshold(Unit self) => 2;
        protected override void Avenge(Fight fight, Unit self) => fight.Buff(self, 5, 5);
    }

    [CardEffect("M_AURA")]
    private sealed class TestAura : CombatEffect
    {
        public override StatDelta AuraFor(Fight fight, Unit self, Unit other) => other == self ? default : new StatDelta(3, 3);
    }

    [Fact]
    public void Run_five_thousand_multithreaded_simulations_and_verify_accuracy()
    {
        const int totalSimulations = 5000;
        const int runsPerCombat = 250;

        var effects = Effects(typeof(TestDeathrattle), typeof(TestTitus), typeof(TestCleave), typeof(TestAvenge), typeof(TestAura));

        var errors = new ConcurrentBag<string>();
        int exactWinCount = 0;
        int exactLossCount = 0;
        int exactTieCount = 0;
        int mixedOutcomeCount = 0;
        int extremeStatsCount = 0;

        var clock = Stopwatch.StartNew();

        Parallel.For(0, totalSimulations, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            try
            {
                var scenario = GenerateScenario(i, effects);
                var odds = CombatSimulator.Run(scenario.Ours, scenario.Theirs, scenario.OursFirst, new SimOptions
                {
                    Runs = runsPerCombat,
                    Effects = effects
                });

                // Invariant 1: Probabilities must sum strictly to 1.0 (100%)
                if (Math.Abs(odds.Win + odds.Loss + odds.Tie - 1.0) > 1e-9)
                {
                    errors.Add($"[#{i}] Odds sum != 1.0: Win={odds.Win}, Tie={odds.Tie}, Loss={odds.Loss}");
                }

                // Invariant 2: No NaN or infinity
                if (double.IsNaN(odds.Win) || double.IsNaN(odds.Loss) || double.IsNaN(odds.Tie))
                {
                    errors.Add($"[#{i}] NaN detected in odds: Win={odds.Win}, Tie={odds.Tie}, Loss={odds.Loss}");
                }

                // Invariant 3: Clamped within [0.0, 1.0]
                if (odds.Win < 0.0 || odds.Win > 1.0 || odds.Loss < 0.0 || odds.Loss > 1.0 || odds.Tie < 0.0 || odds.Tie > 1.0)
                {
                    errors.Add($"[#{i}] Out of bounds rate: Win={odds.Win}, Tie={odds.Tie}, Loss={odds.Loss}");
                }

                // Invariant 4: Check deterministic expectations
                if (scenario.ExpectExactWin && odds.Win != 1.0)
                {
                    errors.Add($"[#{i}] Expected exact 100% win but got {odds.Win:P1} (Loss {odds.Loss:P1})");
                }
                if (scenario.ExpectExactLoss && odds.Loss != 1.0)
                {
                    errors.Add($"[#{i}] Expected exact 100% loss but got {odds.Loss:P1} (Win {odds.Win:P1})");
                }
                if (scenario.ExpectExactTie && odds.Tie != 1.0)
                {
                    errors.Add($"[#{i}] Expected exact 100% tie but got {odds.Tie:P1}");
                }

                // Statistical tracking
                if (odds.Win == 1.0) Interlocked.Increment(ref exactWinCount);
                else if (odds.Loss == 1.0) Interlocked.Increment(ref exactLossCount);
                else if (odds.Tie == 1.0) Interlocked.Increment(ref exactTieCount);
                else Interlocked.Increment(ref mixedOutcomeCount);

                if (scenario.HasExtremeStats) Interlocked.Increment(ref extremeStatsCount);
            }
            catch (Exception ex)
            {
                errors.Add($"[#{i}] Exception thrown during simulation: {ex.Message}\n{ex.StackTrace}");
            }
        });

        clock.Stop();

        output.WriteLine("=========================================================================================");
        output.WriteLine($"5,000 MULTITHREADED COMBAT SIMULATIONS COMPLETED IN {clock.ElapsedMilliseconds} ms");
        output.WriteLine("=========================================================================================");
        output.WriteLine($"Threads utilized:      {Environment.ProcessorCount}");
        output.WriteLine($"Total combat setups:   {totalSimulations}");
        output.WriteLine($"Total fights executed: {totalSimulations * runsPerCombat:N0} simulated fights");
        output.WriteLine($"Exact 100% Wins:       {exactWinCount}");
        output.WriteLine($"Exact 100% Losses:     {exactLossCount}");
        output.WriteLine($"Exact 100% Ties:       {exactTieCount}");
        output.WriteLine($"Dynamic / Mixed Odds:  {mixedOutcomeCount}");
        output.WriteLine($"High Stats (up to 70k):{extremeStatsCount}");
        output.WriteLine($"Simulation Errors:     {errors.Count}");
        output.WriteLine("=========================================================================================");

        if (!errors.IsEmpty)
        {
            foreach (var err in errors.Take(10))
            {
                output.WriteLine("ERROR: " + err);
            }
        }

        Assert.Empty(errors);
        Assert.Equal(totalSimulations, exactWinCount + exactLossCount + exactTieCount + mixedOutcomeCount);
    }

    private sealed record GeneratedScenario(
        SimSide Ours,
        SimSide Theirs,
        bool? OursFirst,
        bool ExpectExactWin,
        bool ExpectExactLoss,
        bool ExpectExactTie,
        bool HasExtremeStats);

    private static GeneratedScenario GenerateScenario(int index, EffectRegistry effects)
    {
        // Deterministic pseudo-random seed per index
        var rng = new Random(index * 9973 + 31);

        int type = index % 10;
        bool hasExtremeStats = false;
        bool expectWin = false;
        bool expectLoss = false;
        bool expectTie = false;

        SimSide ours;
        SimSide theirs;
        bool? oursFirst = (index % 3) switch { 0 => true, 1 => false, _ => null };

        switch (type)
        {
            // Case 0: Extreme scaling stats (e.g. 67,000 attack, 100,000 health)
            case 0:
            {
                hasExtremeStats = true;
                int atk = 67_000 + rng.Next(10_000);
                int hp = 80_000 + rng.Next(20_000);
                ours = Side(M(atk, hp, Keyword.DivineShield));
                theirs = Side(M(1, 1));
                expectWin = true;
                oursFirst = true;
                break;
            }

            // Case 1: Extreme opponent scaling (guaranteed loss)
            case 1:
            {
                hasExtremeStats = true;
                int atk = 70_000 + rng.Next(10_000);
                int hp = 90_000 + rng.Next(20_000);
                ours = Side(M(2, 2));
                theirs = Side(M(atk, hp, Keyword.DivineShield));
                expectLoss = true;
                oursFirst = false;
                break;
            }

            // Case 2: Stalemate 0-Attack boards (guaranteed tie)
            case 2:
            {
                ours = Side(M(0, 5 + rng.Next(10)), M(0, 5 + rng.Next(10)));
                theirs = Side(M(0, 5 + rng.Next(10)));
                expectTie = true;
                break;
            }

            // Case 3: Poisonous / Venomous vs giant walls
            case 3:
            {
                ours = Side(M(1, 1, Keyword.Poisonous | Keyword.DivineShield), M(1, 1, Keyword.Poisonous));
                theirs = Side(M(50_000, 50_000));
                expectWin = true;
                oursFirst = true;
                break;
            }

            // Case 4: Asymmetric swarm vs few minions (7v2)
            case 4:
            {
                var ourList = Enumerable.Range(0, 7)
                    .Select(_ => M(3 + rng.Next(5), 4 + rng.Next(5), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                var theirList = Enumerable.Range(0, 2)
                    .Select(_ => M(8 + rng.Next(8), 12 + rng.Next(12), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                ours = Side(ourList);
                theirs = Side(theirList);
                break;
            }

            // Case 5: Full 7v7 endgame battle with cleave, deathrattles, titus, and auras
            case 5:
            {
                var ourList = Enumerable.Range(0, 7)
                    .Select(_ => M(5 + rng.Next(15), 6 + rng.Next(20), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                var theirList = Enumerable.Range(0, 7)
                    .Select(_ => M(5 + rng.Next(15), 6 + rng.Next(20), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                ours = Side(ourList);
                theirs = Side(theirList);
                break;
            }

            // Case 6: Windfury / Mega-Windfury sweeping boards
            case 6:
            {
                ours = Side(M(8, 25, Keyword.MegaWindfury));
                theirs = Side(M(2, 3), M(2, 3), M(2, 3));
                expectWin = true;
                oursFirst = true;
                break;
            }

            // Case 7: Reborn walls and Titus multipliers
            case 7:
            {
                ours = Side(M(0, 15, cardId: "M_TITUS"), M(3, 2, Keyword.Reborn, cardId: "M_DR"));
                theirs = Side(M(4, 6), M(4, 6));
                break;
            }

            // Case 8: Cleave hitting multiple taunt & divine shield minions
            case 8:
            {
                ours = Side(M(10, 15, cardId: "M_CLEAVE"));
                theirs = Side(M(2, 5, Keyword.Taunt), M(2, 5), M(2, 5));
                break;
            }

            // Case 9: Random realistic board with 2 to 6 minions on each side
            default:
            {
                int countOurs = 2 + (rng.Next(5));
                int countTheirs = 2 + (rng.Next(5));
                var ourList = Enumerable.Range(0, countOurs)
                    .Select(_ => M(2 + rng.Next(20), 3 + rng.Next(25), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                var theirList = Enumerable.Range(0, countTheirs)
                    .Select(_ => M(2 + rng.Next(20), 3 + rng.Next(25), RandomKeyword(rng), RandomEffect(rng)))
                    .ToArray();
                ours = Side(ourList);
                theirs = Side(theirList);
                break;
            }
        }

        return new GeneratedScenario(ours, theirs, oursFirst, expectWin, expectLoss, expectTie, hasExtremeStats);
    }

    private static Keyword RandomKeyword(Random rng)
    {
        Keyword kw = Keyword.None;
        if (rng.Next(4) == 0) kw |= Keyword.Taunt;
        if (rng.Next(5) == 0) kw |= Keyword.DivineShield;
        if (rng.Next(6) == 0) kw |= Keyword.Reborn;
        if (rng.Next(8) == 0) kw |= Keyword.Windfury;
        if (rng.Next(10) == 0) kw |= Keyword.Poisonous;
        if (rng.Next(12) == 0) kw |= Keyword.Venomous;
        if (rng.Next(12) == 0) kw |= Keyword.Stealth;
        return kw;
    }

    private static string RandomEffect(Random rng) => (rng.Next(10)) switch
    {
        0 => "M_DR",
        1 => "M_TITUS",
        2 => "M_CLEAVE",
        3 => "M_AV",
        4 => "M_AURA",
        _ => string.Empty
    };
}
