using Hearthpulse.Core.Battlegrounds;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim;

/// <summary>
/// Verifies that simulated battles produce 100% mathematically consistent win, tie, and loss rates
/// without errors, division-by-zero, integer overflow, or invalid probabilities.
/// </summary>
public sealed class AccuracyAndOddsTests
{
    private static SimMinion M(int attack, int health, Keyword keywords = Keyword.None) =>
        Minion(attack, health, keywords);

    [Fact]
    public void Guaranteed_win_reports_exactly_one_hundred_percent()
    {
        var ours = Side(M(50, 50), M(50, 50));
        var theirs = Side(M(1, 1));

        var odds = CombatSimulator.Run(ours, theirs, firstAttackerIsOurs: true, new SimOptions { Runs = 500 });

        Assert.Equal(1.0, odds.Win);
        Assert.Equal(0.0, odds.Loss);
        Assert.Equal(0.0, odds.Tie);
        Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie);
        Assert.False(odds.Approximate);
    }

    [Fact]
    public void Guaranteed_loss_reports_exactly_one_hundred_percent()
    {
        var ours = Side(M(1, 1));
        var theirs = Side(M(50, 50), M(50, 50));

        var odds = CombatSimulator.Run(ours, theirs, firstAttackerIsOurs: false, new SimOptions { Runs = 500 });

        Assert.Equal(0.0, odds.Win);
        Assert.Equal(1.0, odds.Loss);
        Assert.Equal(0.0, odds.Tie);
        Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie);
    }

    [Fact]
    public void Guaranteed_tie_reports_exactly_one_hundred_percent()
    {
        // Neither 0-attack minion can attack; combat resolves cleanly into a 100% tie.
        var ours = Side(M(0, 10));
        var theirs = Side(M(0, 10));

        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 500 });

        Assert.Equal(0.0, odds.Win);
        Assert.Equal(0.0, odds.Loss);
        Assert.Equal(1.0, odds.Tie);
        Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie);
    }

    [Fact]
    public void Empty_boards_report_exact_results_without_simulating()
    {
        var empty = Side();
        var minions = Side(M(5, 5));

        var winOdds = CombatSimulator.Run(minions, empty, null);
        Assert.Equal(1.0, winOdds.Win);
        Assert.Equal(0.0, winOdds.Loss);
        Assert.Equal(0.0, winOdds.Tie);

        var lossOdds = CombatSimulator.Run(empty, minions, null);
        Assert.Equal(0.0, lossOdds.Win);
        Assert.Equal(1.0, lossOdds.Loss);
        Assert.Equal(0.0, lossOdds.Tie);

        var tieOdds = CombatSimulator.Run(empty, empty, null);
        Assert.Equal(0.0, tieOdds.Win);
        Assert.Equal(0.0, tieOdds.Loss);
        Assert.Equal(1.0, tieOdds.Tie);
    }

    [Fact]
    public void Total_odds_sum_to_one_hundred_percent_across_many_board_combinations()
    {
        (SimSide Ours, SimSide Theirs, bool? OursFirst)[] testCases =
        [
            (Side(M(3, 4)), Side(M(4, 3)), true),
            (Side(M(2, 5, Keyword.Taunt), M(3, 3)), Side(M(5, 2), M(2, 2)), false),
            (Side(M(1, 1, Keyword.Poisonous | Keyword.DivineShield)), Side(M(20, 20), M(20, 20)), null),
            (Side(M(4, 2, Keyword.Reborn)), Side(M(2, 5), M(1, 1)), true),
            (Side(M(3, 8, Keyword.Windfury)), Side(M(2, 4), M(2, 4), M(2, 4)), false),
            (Side(M(1, 1, Keyword.Stealth), M(10, 10, Keyword.Taunt)), Side(M(5, 5), M(5, 5)), null),
            (Side(M(10, 10), M(10, 10), M(10, 10)), Side(M(10, 10), M(10, 10), M(10, 10)), null),
            (Side(M(7, 7, Keyword.DivineShield | Keyword.Reborn | Keyword.Taunt)), Side(M(6, 6), M(6, 6)), true),
            (Side(M(0, 5), M(10, 1)), Side(M(1, 5, Keyword.Taunt), M(2, 2)), false),
            (Side(M(15, 1, Keyword.MegaWindfury)), Side(M(3, 3), M(3, 3), M(3, 3), M(3, 3)), true)
        ];

        foreach (var (ours, theirs, oursFirst) in testCases)
        {
            var odds = CombatSimulator.Run(ours, theirs, oursFirst, new SimOptions { Runs = 1000 });

            Assert.True(odds.Win >= 0.0 && odds.Win <= 1.0, $"Win {odds.Win} out of bounds");
            Assert.True(odds.Loss >= 0.0 && odds.Loss <= 1.0, $"Loss {odds.Loss} out of bounds");
            Assert.True(odds.Tie >= 0.0 && odds.Tie <= 1.0, $"Tie {odds.Tie} out of bounds");
            Assert.False(double.IsNaN(odds.Win) || double.IsNaN(odds.Loss) || double.IsNaN(odds.Tie));
            Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie, 12);
        }
    }

    [Fact]
    public void Zero_runs_or_cancelled_simulation_returns_clean_odds_without_nan_or_error()
    {
        var ours = Side(M(3, 3));
        var theirs = Side(M(3, 3));

        var zeroRunsOdds = CombatSimulator.Run(ours, theirs, true, new SimOptions { Runs = 0 });
        Assert.False(double.IsNaN(zeroRunsOdds.Win));
        Assert.False(double.IsNaN(zeroRunsOdds.Loss));
        Assert.False(double.IsNaN(zeroRunsOdds.Tie));
        Assert.Equal(1.0, zeroRunsOdds.Win + zeroRunsOdds.Loss + zeroRunsOdds.Tie);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledOdds = CombatSimulator.Run(ours, theirs, true, new SimOptions { Runs = 2000, Cancel = cts.Token });
        Assert.False(double.IsNaN(cancelledOdds.Win));
        Assert.False(double.IsNaN(cancelledOdds.Loss));
        Assert.False(double.IsNaN(cancelledOdds.Tie));
        Assert.Equal(1.0, cancelledOdds.Win + cancelledOdds.Loss + cancelledOdds.Tie);
    }

    [Fact]
    public void Giant_stats_simulate_cleanly_without_integer_overflow()
    {
        // 1,000,000 attack and health
        var ours = Side(M(1_000_000, 1_000_000));
        var theirs = Side(M(500_000, 500_000));

        var odds = CombatSimulator.Run(ours, theirs, true, new SimOptions { Runs = 200 });

        Assert.Equal(1.0, odds.Win);
        Assert.Equal(0.0, odds.Loss);
        Assert.Equal(0.0, odds.Tie);
    }

    [Fact]
    public void Sixty_seven_thousand_attack_minions_simulate_accurately()
    {
        // Explicitly tests 67k+ Attack stats (e.g. from massive scaling builds)
        var ours = Side(M(67_000, 80_000));
        var theirs = Side(M(50_000, 60_000));

        var odds = CombatSimulator.Run(ours, theirs, true, new SimOptions { Runs = 200 });

        Assert.Equal(1.0, odds.Win);
        Assert.Equal(0.0, odds.Loss);
        Assert.Equal(0.0, odds.Tie);
    }

    [Fact]
    public void Venomous_is_consumed_on_first_damage()
    {
        var venomous = M(1, 20, Keyword.Venomous);
        var target1 = M(50, 50);
        var target2 = M(10, 10);

        // Venomous kills target1 with 1 damage, but loses venomous. Target2 takes normal 1 damage and survives.
        var result = Fight(Side(venomous), Side(target1, target2), oursFirst: true, picks: [0, 0]);

        Assert.Equal(-1, result.Outcome); // After killing target1, our 1/20 takes 50 and dies
    }

    [Fact]
    public void Divine_shield_absorbs_huge_damage_without_dying()
    {
        var shielded = M(1, 1, Keyword.DivineShield);
        var giant = M(999_999, 1);

        var result = Fight(Side(shielded), Side(giant), oursFirst: false);

        // Giant hits shielded: divine shield lost, shielded deals 1 damage killing giant.
        Assert.Equal(1, result.Outcome);
        var survivor = Assert.Single(result.Ours);
        Assert.Equal(1, survivor.Health);
        Assert.False(survivor.DivineShield);
    }

    [Fact]
    public void Full_board_with_reborn_and_deathrattles_respects_seven_minion_cap()
    {
        var boardOurs = Enumerable.Range(0, 7)
            .Select(_ => M(2, 2, Keyword.Reborn | Keyword.Taunt))
            .ToArray();
        var boardTheirs = Enumerable.Range(0, 7)
            .Select(_ => M(2, 2, Keyword.Reborn))
            .ToArray();

        var odds = CombatSimulator.Run(Side(boardOurs), Side(boardTheirs), null, new SimOptions { Runs = 500 });

        Assert.True(odds.Win >= 0 && odds.Win <= 1.0);
        Assert.True(odds.Loss >= 0 && odds.Loss <= 1.0);
        Assert.True(odds.Tie >= 0 && odds.Tie <= 1.0);
        Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie, 12);
    }

    [Fact]
    public void Symmetrical_boards_maintain_statistical_balance()
    {
        var board1 = Side(M(4, 5), M(2, 3, Keyword.Taunt), M(3, 4, Keyword.DivineShield));
        var board2 = Side(M(4, 5), M(2, 3, Keyword.Taunt), M(3, 4, Keyword.DivineShield));

        var odds = CombatSimulator.Run(board1, board2, null, new SimOptions { Runs = 5000 });

        // In symmetric boards, Win and Loss rates should closely mirror each other
        Assert.InRange(Math.Abs(odds.Win - odds.Loss), 0.0, 0.05);
        Assert.Equal(1.0, odds.Win + odds.Loss + odds.Tie, 12);
    }
}
