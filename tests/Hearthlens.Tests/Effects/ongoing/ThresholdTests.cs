using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>State-dependent keywords and thresholds: Relentless Deflector (Taunt with Divine Shield, Avenge) and Scarlet Survivor.</summary>
public sealed class ThresholdTests
{
    private const string Deflector = "BG34_405";
    private const string Survivor = "BG35_814";

    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    private static List<string> TheirAttacks(FightResult result) =>
        result.Lines("their").Where(l => l.Contains(" attacks ", StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------------------------------- Relentless Deflector

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relentless_deflector_has_taunt_only_while_it_has_divine_shield(bool golden)
    {
        // Ours: a 1/10 and the Deflector (Taunt + Divine Shield). Their 1/50 attacks first, with picks 0, 0, 0.
        // 1) Taunt: the Deflector is the only legal target. Its shield absorbs the hit, so it loses Divine Shield and Taunt.
        // 2) Our 1/10 attacks. 3) Their next attack is free: pick 0 takes our first minion, the 1/10 (now 1/9), not the Deflector.
        var deflector = Minion(5, 4, Keyword.Taunt | Keyword.DivineShield, Deflector, Tribe.Mech, golden);
        var result = Fight(Side(Minion(1, 10), deflector), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(RelentlessDeflector)), 0, 0, 0);

        var attacks = TheirAttacks(result);
        Assert.EndsWith($"attacks our BG34_405{(golden ? "(G)" : string.Empty)} 5/4 Taunt, DivineShield", attacks[0], StringComparison.Ordinal);
        Assert.EndsWith("attacks our minion 1/9", attacks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Relentless_deflector_keeps_a_taunt_it_got_elsewhere()
    {
        // Captured with Taunt but no Divine Shield: that Taunt does not come from the shield and stays.
        var deflector = Minion(5, 4, Keyword.Taunt, Deflector, Tribe.Mech);
        var result = Fight(Side(Minion(1, 10), deflector), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(RelentlessDeflector)), 0);

        Assert.EndsWith("attacks our BG34_405 5/4 Taunt", TheirAttacks(result)[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relentless_deflector_regains_divine_shield_and_taunt_after_three_friendly_deaths(bool golden)
    {
        // Ours: three 0/1 minions, the Deflector (0/4, captured without Divine Shield or Taunt) and a 0/3 bystander.
        // Their 1/50 kills the three 0/1 minions (picks 0): Avenge (3) gives the Deflector Divine Shield, and with it Taunt.
        // Their fourth attack has pick 1, which would hit the bystander if the Deflector had no Taunt; with Taunt the only
        // legal target is the Deflector.
        var deflector = Minion(0, 4, Keyword.None, Deflector, Tribe.Mech, golden);
        var ours = Side(Minion(0, 1), Minion(0, 1), Minion(0, 1), deflector, Minion(0, 3));
        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false, Effects(typeof(RelentlessDeflector)), 0, 0, 0, 1);

        var attacks = TheirAttacks(result);
        Assert.EndsWith($"attacks our BG34_405{(golden ? "(G)" : string.Empty)} 0/4 Taunt, DivineShield", attacks[3], StringComparison.Ordinal);
        var avenge = result.Log.ToList().FindIndex(l => l.StartsWith("avenge: ", StringComparison.Ordinal));
        Assert.True(avenge > 0);
        Assert.Equal(3, result.Log.Take(avenge).Count(l => l.EndsWith(" dies", StringComparison.Ordinal)));
    }

    [Fact]
    public void Relentless_deflector_does_not_avenge_before_the_third_death()
    {
        // Ours: two 0/1 minions, a 0/5, the Deflector (0/4, captured without Divine Shield or Taunt) and a 0/3 at the right.
        // Their 1/50 kills the two 0/1 minions (picks 0). Their third attack has pick 3, clamped to the last minion: with only
        // two deaths there is no shield and no Taunt, so it hits the 0/3. The Avenge only fires after the third death.
        var ours = Side(Minion(0, 1), Minion(0, 1), Minion(0, 5), Minion(0, 4, Keyword.None, Deflector, Tribe.Mech), Minion(0, 3));
        var result = Fight(ours, Side(Minion(1, 50)), oursFirst: false, Effects(typeof(RelentlessDeflector)), 0, 0, 3);

        Assert.EndsWith("attacks our minion 0/3", TheirAttacks(result)[2], StringComparison.Ordinal);
        var avenge = result.Log.ToList().FindIndex(l => l.StartsWith("avenge: ", StringComparison.Ordinal));
        Assert.True(avenge > 0);
        Assert.Equal(3, result.Log.Take(avenge).Count(l => l.EndsWith(" dies", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------------------------------- Scarlet Survivor

    private static SimMinion Pump() => Minion(1, 10, cardId: "TEST_RALLY_PUMP_DRAGONS");

    [Fact]
    public void Scarlet_survivor_gains_divine_shield_once_it_reaches_six_attack()
    {
        // The test Rally gives friendly Dragons +3 Attack when the 1/10 attacks. The 3/3 Survivor becomes 6/3: Divine
        // Shield. Their 0/1 dies to the 1/10's attack.
        var ours = Side(Pump(), Minion(3, 3, cardId: Survivor, tribes: Tribe.Dragon));
        var result = Fight(ours, Side(Minion(0, 1)), oursFirst: true, Effects(typeof(ScarletSurvivor), typeof(RallyPumpDragons)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(1, 10), (6, 3)], Stats(result.Ours));
        Assert.True(result.Ours[1].DivineShield);
    }

    [Fact]
    public void Scarlet_survivor_that_already_has_six_attack_does_not_trigger_again()
    {
        // Captured at 6 Attack it has not "reached" 6 in this combat: +3 makes it 9/3 and gives no Divine Shield.
        var ours = Side(Pump(), Minion(6, 3, cardId: Survivor, tribes: Tribe.Dragon));
        var result = Fight(ours, Side(Minion(0, 1)), oursFirst: true, Effects(typeof(ScarletSurvivor), typeof(RallyPumpDragons)));

        Assert.Equal([(1, 10), (9, 3)], Stats(result.Ours));
        Assert.False(result.Ours[1].DivineShield);
    }

    [Fact]
    public void Scarlet_survivor_below_six_attack_gets_nothing()
    {
        // A Dragon at 2 Attack plus 3 is 5: below the threshold.
        var ours = Side(Pump(), Minion(2, 3, cardId: Survivor, tribes: Tribe.Dragon));
        var result = Fight(ours, Side(Minion(0, 1)), oursFirst: true, Effects(typeof(ScarletSurvivor), typeof(RallyPumpDragons)));

        Assert.Equal([(1, 10), (5, 3)], Stats(result.Ours));
        Assert.False(result.Ours[1].DivineShield);
    }

    [Fact]
    public void Golden_scarlet_survivor_gains_divine_shield_when_it_comes_back_up_to_six()
    {
        // Golden Survivor (printed 6/6) that was reduced to 4 Attack: +3 makes 7/6, which reaches 6: Divine Shield.
        var survivor = Minion(6, 6, cardId: Survivor, tribes: Tribe.Dragon, golden: true) with { Attack = 4 };
        var result = Fight(Side(Pump(), survivor), Side(Minion(0, 1)), oursFirst: true, Effects(typeof(ScarletSurvivor), typeof(RallyPumpDragons)));

        Assert.Equal([(1, 10), (7, 6)], Stats(result.Ours));
        Assert.True(result.Ours[1].DivineShield);
    }
}
