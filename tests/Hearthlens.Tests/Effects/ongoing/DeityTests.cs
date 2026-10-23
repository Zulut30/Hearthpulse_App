using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>The two Deities (C'Thun and Y'Shaarj) awakening after four friendly Aberration deaths.</summary>
public sealed class DeityTests
{
    private const string AttackTag = "BACON_OLD_GOD_ATTACK";
    private const string HealthTag = "BACON_OLD_GOD_HEALTH";

    private static SimSide DeitySide(string deity, int attack, int health, params SimMinion[] minions) =>
        new(minions)
        {
            Extras = [deity],
            Tags = new Dictionary<string, int> { [AttackTag] = attack, [HealthTag] = health },
        };

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) =>
        minions.Select(m => (m.Attack, m.Health)).ToList();

    [Fact]
    public void Both_deities_are_registered()
    {
        Assert.True(EffectRegistry.Default.Implements(Tokens.YShaarjId, golden: false));
        Assert.True(EffectRegistry.Default.Implements(Tokens.YShaarjId, golden: true));
        Assert.True(EffectRegistry.Default.Implements(Tokens.CThunId, golden: false));
        Assert.True(EffectRegistry.Default.Implements(Tokens.CThunId, golden: true));
    }

    [Fact]
    public void Yshaarj_awakens_after_four_aberrations_die_with_the_players_stats()
    {
        // Four 0/1 Aberrations die to their 1/50. The sleeping Y'Shaarj has 3/2 from the player tags and is summoned
        // after the fourth death.
        var ours = DeitySide(
            Tokens.YShaarjId, 3, 2,
            Minion(0, 1, cardId: "AB1", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB2", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB3", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB4", tribes: Tribe.Aberration));

        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false, Effects(typeof(YShaarj)));

        var awaken = result.IndexOf("Deity BGFYM_011 awakens");
        Assert.True(awaken > 0);
        Assert.Equal(4, result.Log.Take(awaken).Count(l => l.EndsWith(" dies", StringComparison.Ordinal)));
        Assert.Contains(result.Log, l => l.Contains("summoned our BGFYM_011 3/2", StringComparison.Ordinal));
    }

    [Fact]
    public void Yshaarj_resummons_the_first_two_dead_aberrations()
    {
        // Two Aberrations die, then the TEST_DR_YSHAARJ minion (1/1) dies and summons Y'Shaarj. When that Y'Shaarj dies,
        // its deathrattle brings back the first two Aberrations that died this combat.
        var ours = Side(
            Minion(0, 1, cardId: "AB1", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB2", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "TEST_DR_YSHAARJ", mechanics: CardMechanic.Deathrattle));

        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false,
                           Effects(typeof(YShaarj), typeof(DrSummonYShaarj)));

        Assert.Contains(result.Log, l => l.Contains("summoned our BGFYM_011 1/1", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("deathrattle of our BGFYM_011", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("summoned our AB1 0/1", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("summoned our AB2 0/1", StringComparison.Ordinal));
    }

    [Fact]
    public void Cthun_splits_its_stats_when_it_awakens()
    {
        // Their 1/1 kills the four 0/1 Aberrations. C'Thun awakens as 2/2 and gives its stats to the only other minion:
        // the 0/10 bystander becomes 2/12. C'Thun then kills the 1/1.
        var ours = DeitySide(
            Tokens.CThunId, 2, 2,
            Minion(0, 1, cardId: "AB1", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB2", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB3", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB4", tribes: Tribe.Aberration),
            Minion(0, 10, cardId: "WALL"));

        var result = Fight(ours, Side(Minion(1, 1)), oursFirst: false, Effects(typeof(CThun)));

        Assert.Equal(1, result.Outcome);
        // Their 1/1 gets one more attack after the awakening and takes 1 Health off the wall; C'Thun itself is 2/2.
        Assert.Equal([(2, 11), (2, 2)], Stats(result.Ours));
    }

    [Fact]
    public void Reported_aberration_board_with_yshaarj_is_not_a_certain_loss()
    {
        // The exact turn-3 board from the reported game: three Aberrations vs a 9/7 and a 3/3. Underrot Spawn's token is
        // the fourth Aberration death, so Y'Shaarj awakens with the player's 3/2. The old simulator ignored the Deity
        // extra and reported a certain loss; this board must keep a real chance to win.
        var ours = new SimSide(
        [
            Minion(3, 2, cardId: "BG36_098", tribes: Tribe.Aberration) with { EntityId = 1084 },
            Minion(2, 3, cardId: "BG36_110", tribes: Tribe.Aberration),
            Minion(2, 2, cardId: "BG36_116", tribes: Tribe.Aberration, mechanics: CardMechanic.Deathrattle) with { ScriptData1 = 1 },
        ])
        {
            Extras = [Tokens.YShaarjId],
            Tags = new Dictionary<string, int> { [AttackTag] = 3, [HealthTag] = 2 },
        };
        var theirs = new SimSide(
        [
            Minion(9, 7, cardId: "BG26_174"),
            Minion(3, 3, cardId: "BG36_345"),
        ]);

        var odds = CombatSimulator.Run(ours, theirs, firstAttackerIsOurs: true,
                                       new SimOptions { Runs = 20_000, FirstAttackerEntityId = 1084 });

        Assert.True(odds.Win > 0);
        Assert.True(odds.Loss < 1);
        Assert.Equal(0, odds.Unsimulated);
    }

    [Fact]
    public void Drifting_sacrifice_buffs_the_sleeping_deity_twice_through_reborn()
    {
        // Drifting Sacrifice dies, gives +2/+1, comes back with Reborn and dies again for another +2/+1. The two other
        // Aberration deaths are the third and fourth, so Y'Shaarj awakens at 5/3.
        var ours = DeitySide(
            Tokens.YShaarjId, 1, 1,
            Minion(1, 1, Keyword.Reborn, "BG36_113", Tribe.Aberration, mechanics: CardMechanic.Deathrattle),
            Minion(0, 1, cardId: "AB1", tribes: Tribe.Aberration),
            Minion(0, 1, cardId: "AB2", tribes: Tribe.Aberration));

        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false,
                           Effects(typeof(YShaarj), typeof(DriftingSacrifice)));

        Assert.Contains(result.Log, l => l.Contains("our Deity gets +2/+1 (3/2)", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("our Deity gets +2/+1 (5/3)", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("summoned our BGFYM_011 5/3", StringComparison.Ordinal));
    }

    [Fact]
    public void Faceless_converter_uses_its_captured_script_values()
    {
        // Printed +2/+1, but the captured text variables are +5/+3 (improved by Tavern spells).
        var ours = DeitySide(
            Tokens.YShaarjId, 1, 1,
            Minion(2, 2, cardId: "BG36_318", tribes: Tribe.Aberration, mechanics: CardMechanic.Deathrattle) with
            {
                ScriptData1 = 5,
                ScriptData2 = 3,
            });

        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false,
                           Effects(typeof(YShaarj), typeof(FacelessConverter)));

        Assert.Contains(result.Log, l => l.Contains("our Deity gets +5/+3 (6/4)", StringComparison.Ordinal));
    }

    [Fact]
    public void Cthrax_wrecker_buffs_the_deity_when_it_attacks_and_when_it_dies()
    {
        var ours = DeitySide(
            Tokens.YShaarjId, 1, 1,
            Minion(4, 4, cardId: "BGDUO_701", mechanics: CardMechanic.Deathrattle));

        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: true,
                           Effects(typeof(YShaarj), typeof(CThraxWrecker)));

        Assert.Contains(result.Log, l => l.Contains("our Deity gets +4/+4 (5/5)", StringComparison.Ordinal));
        Assert.Contains(result.Log, l => l.Contains("our Deity gets +4/+4 (9/9)", StringComparison.Ordinal));
    }

    [Fact]
    public void Unregistered_deity_extra_is_marked_approximate()
    {
        // The old registration gap: an extra with no effect used to be ignored without making the odds approximate.
        var ours = new SimSide([Minion(1, 1)]) { Extras = [Tokens.YShaarjId] };
        var odds = CombatSimulator.Run(ours, Side(Minion(1, 1)), firstAttackerIsOurs: null,
                                       new SimOptions { Runs = 100, Effects = EffectRegistry.Empty });

        Assert.True(odds.Approximate);
        Assert.True(odds.Unsimulated >= 1);
    }
}
