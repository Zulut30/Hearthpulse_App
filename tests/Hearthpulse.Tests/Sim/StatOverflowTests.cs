using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim;

/// <summary>Stats near <see cref="int.MaxValue"/> are clamped instead of wrapping around to negative numbers.</summary>
public sealed class StatOverflowTests
{
    private const int Huge = int.MaxValue - 5;

    [CardEffect("T_SELF_BUFF")]
    private sealed class SelfBuff : CombatEffect
    {
        public override void OnFightStart(Fight fight, Unit self) => fight.Buff(self, 10, 10);
    }

    [CardEffect("T_AURA")]
    private sealed class PlusTenAura : CombatEffect
    {
        public override StatDelta AuraFor(Fight fight, Unit self, Unit other) => other == self ? default : new StatDelta(10, 10);
    }

    [CardEffect("T_SUMMON_AURA")]
    private sealed class SummonAura : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Minion(1, 1, cardId: "T_AURA"), 1);
    }

    [Fact]
    public void Buff_on_a_huge_minion_stops_at_int_max()
    {
        var result = Fight(Side(Minion(Huge, Huge, cardId: "T_SELF_BUFF")), Side(Minion(1, 1)), effects: Effects(typeof(SelfBuff)));

        Assert.Equal(1, result.Outcome);
        var survivor = Assert.Single(result.Ours);
        Assert.Equal(int.MaxValue, survivor.Attack);
        Assert.True(survivor.Health > 0);
    }

    [Fact]
    public void Aura_arriving_next_to_a_huge_minion_stops_at_int_max()
    {
        // The deathrattle summons the aura minion mid-fight, so the aura is applied to the giant as a +10/+10 change.
        var result = Fight(
            Side(Minion(1, 1, cardId: "T_SUMMON_AURA"), Minion(Huge, Huge)),
            Side(Minion(1, 100)),
            effects: Effects(typeof(PlusTenAura), typeof(SummonAura)));

        Assert.Equal(1, result.Outcome);
        // Clamped at int.MaxValue when the aura arrives, minus 10 once the aura minion dies; a wrapped sum ends far below.
        var giant = Assert.Single(result.Ours);
        Assert.Equal(int.MaxValue - 10, giant.Attack);
        Assert.True(giant.Health > 0);
    }
}