using System.Diagnostics;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Xunit.Abstractions;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim;

/// <summary>The combat pipeline with synthetic test-only effects (no real card implementations involved).</summary>
public sealed class EngineTests(ITestOutputHelper output)
{
    private static readonly SimMinion Token = Minion(1, 1, cardId: "TEST_TOKEN");

    // ------------------------------------------------------------------------------------------- reborn

    [Fact]
    public void Reborn_copy_is_the_base_card_with_one_health_and_its_own_keywords()
    {
        // Printed 2/1 Divine Shield + Reborn, buffed to 10/10 with Taunt, Poisonous and Windfury and its shield used.
        var printed = Minion(2, 1, Keyword.DivineShield | Keyword.Reborn, "TEST_R");
        var buffed = printed with { Attack = 10, Health = 10, MaxHealth = 10, Taunt = true, Poisonous = true, AttacksPerTurn = 2, DivineShield = false };
        // Their 20/200 attacks first: both die (poison), and only our reborn copy is left.
        var result = Fight(Side(buffed), Side(Minion(20, 200, cardId: "BIG")), oursFirst: false);

        Assert.Equal(1, result.Outcome);
        var copy = Assert.Single(result.Ours);
        Assert.Equal((2, 1), (copy.Attack, copy.Health));
        Assert.True(copy.DivineShield);
        Assert.False(copy.Taunt);
        Assert.False(copy.Poisonous);
        Assert.False(copy.Reborn);
        Assert.Equal(1, copy.AttacksPerTurn);
    }

    [Fact]
    public void Golden_reborn_copy_has_golden_printed_attack()
    {
        var printed = Minion(4, 2, Keyword.Reborn, "TEST_R", golden: true);
        var buffed = printed with { Attack = 9, Health = 9, MaxHealth = 9, Poisonous = true };
        var result = Fight(Side(buffed), Side(Minion(20, 200)), oursFirst: false);

        var copy = Assert.Single(result.Ours);
        Assert.True(copy.Golden);
        Assert.Equal((4, 1), (copy.Attack, copy.Health));
    }

    [Fact]
    public void Reborn_copy_of_the_attacker_waits_and_the_next_minion_attacks()
    {
        // Our R attacks and dies; its copy takes R's slot left of the attack gap, so B attacks next, not the copy.
        var r = Minion(2, 1, Keyword.Reborn, "R");
        var b = Minion(3, 5, cardId: "B");
        // Picks: our R → E; their E → B (index 1); then our next attacker.
        var result = Fight(Side(r, b), Side(Minion(1, 20, cardId: "E")), oursFirst: true, picks: [0, 1, 0]);

        var attacks = result.Lines("our ").Where(l => l.Contains(" attacks ", StringComparison.Ordinal)).ToList();
        Assert.StartsWith("our R ", attacks[0]);
        Assert.StartsWith("our B ", attacks[1]);
    }

    [Fact]
    public void Reborn_copy_of_the_minion_due_to_attack_next_attacks_next()
    {
        // Their L attacks first, so their D is next in line. We kill D; its reborn copy appears right of the gap
        // (right of L) and attacks next, before Z. (The simulator's previous behaviour; unverified against real logs.)
        var l = Minion(1, 30, cardId: "L");
        var d = Minion(2, 1, Keyword.Reborn, "D");
        var z = Minion(1, 30, cardId: "Z");
        // Picks: their L → O; our O → D (index 1); their next attacker → O.
        var result = Fight(Side(Minion(5, 100, cardId: "O")), Side(l, d, z), oursFirst: false, picks: [0, 1, 0]);

        var theirAttacks = result.Lines("their ").Where(l2 => l2.Contains(" attacks ", StringComparison.Ordinal)).ToList();
        Assert.StartsWith("their L ", theirAttacks[0]);
        Assert.StartsWith("their D ", theirAttacks[1]);
        Assert.True(result.IndexOf("reborn their D 2/1 at 1") >= 0);
    }

    // ------------------------------------------------------------------------------------------- deaths

    [Fact]
    public void Deathrattle_summons_take_the_dead_minions_slot_and_wait_for_the_next_round()
    {
        var effects = Effects(typeof(SummonTwo));
        // Our DR attacks first and dies to E; two tokens appear in its slot, left of B, so B attacks next and the
        // tokens attack once the order wraps around. E always hits B (pick 2).
        var result = Fight(Side(Minion(1, 1, cardId: "DR"), Minion(1, 9, cardId: "B")), Side(Minion(1, 50, cardId: "E")), true, effects,
                           picks: [0, 2, 0, 2, 0]);

        Assert.Equal(2, result.Lines("summoned our TEST_TOKEN 1/1 at 0").Count + result.Lines("summoned our TEST_TOKEN 1/1 at 1").Count);
        var ourAttacks = result.Lines("our ").Where(l => l.Contains(" attacks ", StringComparison.Ordinal)).ToList();
        Assert.StartsWith("our DR ", ourAttacks[0]);
        Assert.StartsWith("our B ", ourAttacks[1]);
        Assert.StartsWith("our TEST_TOKEN ", ourAttacks[2]);
    }

    [Fact]
    public void Summons_beyond_seven_minions_are_lost()
    {
        var effects = Effects(typeof(SummonTwo));
        var board = new[] { Minion(1, 1, cardId: "DR") }.Concat(Enumerable.Range(0, 6).Select(i => Minion(0, 5, cardId: "F" + i))).ToArray();
        var result = Fight(Side(board), Side(Minion(1, 50, cardId: "E")), true, effects);

        var firstDeath = result.IndexOf("our DR 1/0 dies");
        Assert.True(firstDeath >= 0);
        Assert.Contains("summoned our TEST_TOKEN 1/1 at 0", result.Log[firstDeath + 2]);
        Assert.Contains("no room to summon TEST_TOKEN", result.Log[firstDeath + 3]);
    }

    [Fact]
    public void Deaths_resolve_attacking_side_first_then_left_to_right()
    {
        var effects = Effects(typeof(SummonTwo), typeof(Cleave));
        // Our cleaving attacker hits their middle minion (pick 1) and both neighbours; it dies too.
        var result = Fight(
            Side(Minion(1, 1, cardId: "CLEAVE")),
            Side(Minion(1, 1, cardId: "X"), Minion(1, 1, cardId: "Y"), Minion(1, 1, cardId: "Z")),
            true, effects, picks: [1]);

        var deaths = result.Log.Where(l => l.EndsWith(" dies", StringComparison.Ordinal)).Take(4).ToList();
        Assert.Equal(["our CLEAVE 1/0 dies", "their X 1/0 dies", "their Y 1/0 dies", "their Z 1/0 dies"], deaths);
    }

    [Fact]
    public void Deathrattle_multiplier_repeats_deathrattles()
    {
        var effects = Effects(typeof(SummonTwo), typeof(ExtraTrigger));
        // Their E kills our DR (pick 1); with the Titus-like minion alive its deathrattle runs twice: four tokens.
        var result = Fight(Side(Minion(0, 10, cardId: "TITUS"), Minion(1, 1, cardId: "DR")), Side(Minion(1, 100, cardId: "E")),
                           false, effects, picks: [1]);

        var death = result.IndexOf("our DR 1/0 dies");
        Assert.Equal(2, result.Log.Skip(death).Count(l => l.StartsWith("deathrattle of our DR", StringComparison.Ordinal)));
        Assert.Equal(4, result.Log.Skip(death).Take(8).Count(l => l.StartsWith("summoned our TEST_TOKEN", StringComparison.Ordinal)));
    }

    [Fact]
    public void Reborn_comes_after_the_deathrattle_to_the_right_of_its_summons()
    {
        var effects = Effects(typeof(SummonTwo));
        var result = Fight(Side(Minion(1, 1, Keyword.Reborn, "DR"), Minion(0, 5, cardId: "B")), Side(Minion(1, 100, cardId: "E")), true, effects);

        var death = result.IndexOf("our DR 1/0 Reborn dies");
        Assert.Contains("summoned our TEST_TOKEN 1/1 at 0", result.Log[death + 2]);
        Assert.Contains("summoned our TEST_TOKEN 1/1 at 1", result.Log[death + 3]);
        Assert.Equal("reborn our DR 1/1 at 2", result.Log[death + 4]);
    }

    [Fact]
    public void Avenge_fires_every_x_friendly_deaths()
    {
        var effects = Effects(typeof(AvengeTwo));
        // Our A dies attacking E; E kills B (pick 1): two deaths, so AV gains +5/+5, then AV (5/15) beats E.
        var result = Fight(
            Side(Minion(0, 10, cardId: "AV"), Minion(1, 1, cardId: "A"), Minion(1, 1, cardId: "B")),
            Side(Minion(3, 12, cardId: "E")),
            true, effects, picks: [0, 1, 0, 0]);

        Assert.Equal(1, result.Outcome);
        var av = Assert.Single(result.Ours);
        Assert.Equal((5, 9), (av.Attack, av.Health));
    }

    [Fact]
    public void Destroy_effects_know_the_killer()
    {
        var effects = Effects(typeof(DestroyKiller));
        // Their E kills our Leeroy-like minion, whose deathrattle destroys E; their F is left.
        var result = Fight(Side(Minion(1, 1, cardId: "LEEROY")), Side(Minion(5, 5, cardId: "E"), Minion(5, 5, cardId: "F")), false, effects);

        Assert.Contains("their E 5/4 is destroyed", result.Log);
        var survivor = Assert.Single(result.Theirs);
        Assert.Equal("F", survivor.CardId);
    }

    // ------------------------------------------------------------------------------------------- attacks

    [Fact]
    public void Rally_happens_before_damage()
    {
        var effects = Effects(typeof(RallyPlusTen));
        // +10 Attack before the swing: our 1/20 one-shots the 0/5.
        var result = Fight(Side(Minion(1, 20, cardId: "RALLY")), Side(Minion(0, 5, cardId: "E")), true, effects);

        Assert.Equal(1, result.Outcome);
        Assert.Single(result.Log, l => l.Contains(" attacks ", StringComparison.Ordinal));
        Assert.Equal(11, Assert.Single(result.Ours).Attack);
    }

    [Fact]
    public void Attack_is_cancelled_when_a_rally_kills_the_target()
    {
        var effects = Effects(typeof(RallyKillsTarget));
        var result = Fight(Side(Minion(1, 1, cardId: "RK")), Side(Minion(5, 5, cardId: "E"), Minion(5, 5, cardId: "F")), true, effects);

        var cancelled = result.IndexOf("attack cancelled");
        Assert.True(cancelled > 0);
        // Our 1/1 took no damage from the cancelled attack.
        Assert.DoesNotContain(result.Log.Take(cancelled), l => l.StartsWith("our RK", StringComparison.Ordinal) && l.Contains(" takes ", StringComparison.Ordinal));
    }

    [Fact]
    public void Attacks_soon_happen_before_the_next_regular_attack()
    {
        var effects = Effects(typeof(SummonAttacker));
        var result = Fight(Side(Minion(1, 1, cardId: "SOON")), Side(Minion(1, 3, cardId: "E")), true, effects);

        var summoned = result.IndexOf("summoned our TEST_TOKEN");
        Assert.True(summoned > 0);
        Assert.StartsWith("our TEST_TOKEN 1/1 attacks their E", result.Log.Skip(summoned).First(l => l.Contains(" attacks ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Damage_hooks_fire_after_combat_damage()
    {
        var effects = Effects(typeof(GainAttackWhenDamaged));
        // Our 1/10 takes 3, gains +3 Attack, and is a 4/7 after the first exchange.
        var result = Fight(Side(Minion(1, 10, cardId: "HURT")), Side(Minion(3, 1, cardId: "E")), true, effects);

        var hurt = Assert.Single(result.Ours);
        Assert.Equal((4, 7), (hurt.Attack, hurt.Health));
    }

    // ------------------------------------------------------------------------------------------- summons and auras

    [Fact]
    public void Whenever_summon_hooks_run_before_after_summon_hooks_and_lingering_buffs_first()
    {
        var effects = Effects(typeof(SummonWatcher), typeof(SelfDestructSummonTwo));
        // The tokens get +1 Attack (lingering), +1 ("whenever"), then double ("after"): 1 → 2 → 3 → 6.
        var result = Fight(Side(Minion(0, 10, cardId: "WATCH"), Minion(1, 1, cardId: "BOOM")), Side(Minion(0, 60, cardId: "E")), true, effects);

        Assert.Equal(1, result.Outcome);
        Assert.Equal([0, 6, 6], result.Ours.Select(m => m.Attack));
    }

    [Fact]
    public void Aura_from_a_dead_minion_is_removed_and_health_capped()
    {
        var effects = Effects(typeof(AuraPlusTwo));
        // U is printed 1/1; captured as 3/2 (aura +2/+2, 1 damage). The aura minion trades with E and dies.
        var u = Minion(1, 1, cardId: "U") with { Attack = 3, Health = 2, MaxHealth = 3 };
        var result = Fight(Side(Minion(1, 1, cardId: "AURA"), u), Side(Minion(1, 1, cardId: "E")), true, effects);

        var survivor = Assert.Single(result.Ours);
        Assert.Equal((1, 1, 1), (survivor.Attack, survivor.Health, survivor.MaxHealth));
    }

    [Fact]
    public void Hand_effects_are_only_simulated_for_the_local_player()
    {
        var effects = Effects(typeof(NeedsHandEffect));
        Assert.True(effects.Implements("HAND", golden: false, isOpponent: false));
        Assert.False(effects.Implements("HAND", golden: false, isOpponent: true));
    }

    // ------------------------------------------------------------------------------------------- determinism and speed

    [Fact]
    public void Seed_is_stable_across_processes()
    {
        // FNV-1a over the board; this constant must not change between runs, machines or .NET versions.
        var seed = CombatSimulator.Seed(Side(Minion(3, 3, Keyword.Taunt, "BG_TEST")), Side(Minion(2, 4)), true);
        Assert.Equal(745727955, seed);
    }

    [Fact]
    public void Same_boards_give_the_same_numbers()
    {
        var ours = Side(Minion(3, 3), Minion(2, 4, Keyword.DivineShield));
        var theirs = Side(Minion(3, 3), Minion(2, 4, Keyword.Taunt));
        Assert.Equal(CombatSimulator.Run(ours, theirs, null), CombatSimulator.Run(ours, theirs, null));
    }

    [Fact]
    public void Full_boards_with_effects_run_fast_enough()
    {
        var effects = Effects(typeof(SummonTwo), typeof(ExtraTrigger), typeof(RallyPlusTen), typeof(AvengeTwo), typeof(AuraPlusTwo),
                              typeof(SummonWatcher), typeof(GainAttackWhenDamaged), typeof(Cleave));
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

    [Fact]
    public void Time_budget_stops_early_but_keeps_minimum_runs()
    {
        var effects = Effects(typeof(SummonTwo));
        var odds = CombatSimulator.Run(
            Side(Minion(4, 6, cardId: "DR"), Minion(4, 6, cardId: "DR")),
            Side(Minion(4, 6, cardId: "DR"), Minion(4, 6, cardId: "DR")),
            null,
            new SimOptions { Runs = 1_000_000, TimeBudget = TimeSpan.FromMilliseconds(1), MinRuns = 300, Effects = effects });

        Assert.InRange(odds.Runs, 300, 999_999);
        Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 6);
    }

    // ------------------------------------------------------------------------------------------- test-only effects

    [CardEffect("DR")]
    private sealed class SummonTwo : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Token, 2);
    }

    [CardEffect("TITUS")]
    private sealed class ExtraTrigger : CombatEffect
    {
        public override int ExtraDeathrattles(Fight fight, Unit self) => self.Golden ? 2 : 1;
    }

    [CardEffect("CLEAVE")]
    private sealed class Cleave : CombatEffect
    {
        public override void OnAttackDamage(Fight fight, Unit self, Unit target)
        {
            var (left, right) = fight.Neighbours(target);
            if (left is not null) fight.DealDamage(self, left, self.Attack);
            if (right is not null) fight.DealDamage(self, right, self.Attack);
        }
    }

    [CardEffect("AV")]
    private sealed class AvengeTwo : AvengeEffect
    {
        protected override int Threshold(Unit self) => 2;

        protected override void Avenge(Fight fight, Unit self) => fight.Buff(self, 5, 5);
    }

    [CardEffect("LEEROY")]
    private sealed class DestroyKiller : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self)
        {
            if (self.Killer is { IsAlive: true } killer)
            {
                fight.Destroy(killer, self);
            }
        }
    }

    [CardEffect("RALLY")]
    private sealed class RallyPlusTen : CombatEffect
    {
        public override void OnAttack(Fight fight, Unit self, Unit target) => fight.Buff(self, 10, 0);
    }

    [CardEffect("RK")]
    private sealed class RallyKillsTarget : CombatEffect
    {
        public override void OnAttack(Fight fight, Unit self, Unit target) => fight.DealDamage(self, target, 100);
    }

    [CardEffect("SOON")]
    private sealed class SummonAttacker : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self)
        {
            if (fight.SummonAt(self, Token) is { } token)
            {
                fight.AttackSoon(token);
            }
        }
    }

    [CardEffect("HURT")]
    private sealed class GainAttackWhenDamaged : CombatEffect
    {
        public override void OnDamaged(Fight fight, Unit self, Unit? source, int amount) => fight.Buff(self, amount, 0);
    }

    [CardEffect("WATCH")]
    private sealed class SummonWatcher : CombatEffect
    {
        public override void OnFightStart(Fight fight, Unit self) => self.Side.AddSummonBuff(Tribe.All, 1, 0);

        public override void WheneverFriendlySummoned(Fight fight, Unit self, Unit summoned) => fight.Buff(summoned, 1, 0);

        public override void AfterFriendlySummoned(Fight fight, Unit self, Unit summoned) => fight.Buff(summoned, summoned.Attack, 0);
    }

    [CardEffect("BOOM")]
    private sealed class SelfDestructSummonTwo : CombatEffect
    {
        public override void OnAttack(Fight fight, Unit self, Unit target) => fight.Destroy(self);

        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Token, 2);
    }

    [CardEffect("AURA")]
    private sealed class AuraPlusTwo : CombatEffect
    {
        public override StatDelta AuraFor(Fight fight, Unit self, Unit other) => other == self ? default : new StatDelta(2, 2);
    }

    [CardEffect("HAND")]
    private sealed class NeedsHandEffect : CombatEffect
    {
        public override bool NeedsHand => true;
    }
}
