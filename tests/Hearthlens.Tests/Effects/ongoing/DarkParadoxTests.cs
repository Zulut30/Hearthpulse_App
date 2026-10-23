using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Ongoing;

/// <summary>The two combat-relevant Dark Paradox forms and their Dark Gift enchantments.</summary>
public sealed class DarkParadoxTests
{
    [Fact]
    public void Dark_paradox_forms_and_enchantments_are_registered()
    {
        Assert.True(EffectRegistry.Default.Implements("BG36_360t4", golden: false));
        Assert.True(EffectRegistry.Default.Implements("BG36_360_Gt4", golden: true));
        Assert.True(EffectRegistry.Default.Implements("BG36_MidGameEffect_000t15e", golden: false));
        Assert.True(EffectRegistry.Default.Implements("BG36_360t9", golden: false));
        Assert.True(EffectRegistry.Default.Implements("BG36_360_Gt9", golden: true));
        Assert.True(EffectRegistry.Default.Implements("BG36_MidGameEffect_000t61e", golden: false));
    }

    [Fact]
    public void Dark_paradox_shield_absorbs_three_hits()
    {
        var shield = Minion(0, 10, Keyword.DivineShield, "BG36_360t4") with { ScriptData1 = 3 };
        var result = Fight(Side(shield), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(DarkParadoxShield)));

        Assert.Equal(2, result.Log.Count(l => l.Contains("Divine Shield holds", StringComparison.Ordinal)));
        Assert.Equal(3, result.Log.Count(l => l.Contains("loses Divine Shield", StringComparison.Ordinal)));
    }

    [Fact]
    public void Toreths_blessing_also_absorbs_three_hits()
    {
        var shield = Minion(0, 10, Keyword.DivineShield, "ANY") with
        {
            Enchantments = ["BG36_MidGameEffect_000t15e"],
        };
        var result = Fight(Side(shield), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(TorethsBlessing)));

        Assert.Equal(2, result.Log.Count(l => l.Contains("Divine Shield holds", StringComparison.Ordinal)));
        Assert.Equal(3, result.Log.Count(l => l.Contains("loses Divine Shield", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dark_paradox_golem_summons_a_golem_with_the_minions_stats()
    {
        var paradox = Minion(4, 5, cardId: "BG36_360t9", mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(paradox), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(DarkParadoxGolem)));

        Assert.Contains(result.Log, l => l.Contains("summoned our BG29_864t 4/5", StringComparison.Ordinal));
    }

    [Fact]
    public void Golden_dark_paradox_golem_doubles_the_stats()
    {
        var paradox = Minion(8, 10, cardId: "BG36_360_Gt9", golden: true, mechanics: CardMechanic.Deathrattle);
        var result = Fight(Side(paradox), Side(Minion(20, 100)), oursFirst: false, Effects(typeof(DarkParadoxGolem)));

        Assert.Contains(result.Log, l => l.Contains("summoned our BG29_864t 16/20", StringComparison.Ordinal));
    }

    [Fact]
    public void Golemancy_enchantment_summons_a_golem_with_the_minions_stats()
    {
        var minion = Minion(3, 4, cardId: "ANY", mechanics: CardMechanic.Deathrattle) with
        {
            Enchantments = ["BG36_MidGameEffect_000t61e"],
        };
        var result = Fight(Side(minion), Side(Minion(1, 50)), oursFirst: false, Effects(typeof(Golemancy)));

        Assert.Contains(result.Log, l => l.Contains("summoned our BG29_864t 3/4", StringComparison.Ordinal));
    }
}
