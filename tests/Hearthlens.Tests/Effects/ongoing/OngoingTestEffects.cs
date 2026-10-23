using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Ongoing;

namespace Hearthlens.Tests.Sim.Ongoing;

// Small effects only the ongoing-family tests use: they create the summons, deathrattle triggers and buffs that the
// cards under test react to. They are passed to Effects(...) together with the effect under test; the default registry
// never sees them (it only scans Hearthlens.Core).

/// <summary>Ids and builders shared by the ongoing-family tests.</summary>
internal static class Ids
{
    public const string BeastToken = "TEST_BEAST";
    public const string MechToken = "TEST_MECH";

    /// <summary>A 2/3 Beast token (what the test deathrattles summon).</summary>
    public static SimMinion Beast() => Tokens.Make(BeastToken, 2, 3, Tribe.Beast);

    public static SimMinion Mech() => Tokens.Make(MechToken, 2, 3, Tribe.Mech);
}

/// <summary>Deathrattle: summon a 2/3 Beast.</summary>
[CardEffect("TEST_DR_BEAST")]
internal sealed class DrSummonBeast : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => fight.SummonAt(self, Ids.Beast());
}

/// <summary>Deathrattle: summon a 2/3 Beast, then a 2/3 Mech, left to right.</summary>
[CardEffect("TEST_DR_BEAST_MECH")]
internal sealed class DrSummonBeastAndMech : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var beast = fight.SummonAt(self, Ids.Beast());
        if (beast is not null)
        {
            fight.SummonAt(beast, Ids.Mech());
        }
    }
}

/// <summary>Deathrattle: summon two 2/3 Beasts, left to right.</summary>
[CardEffect("TEST_DR_TWO_BEASTS")]
internal sealed class DrSummonTwoBeasts : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        SummonOrder.SummonAll(fight, self, [Ids.Beast(), Ids.Beast()]);
}

/// <summary>Deathrattle: summon four 2/3 Beasts, left to right.</summary>
[CardEffect("TEST_DR_FOUR_BEASTS")]
internal sealed class DrSummonFourBeasts : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        SummonOrder.SummonAll(fight, self, [Ids.Beast(), Ids.Beast(), Ids.Beast(), Ids.Beast()]);
}

/// <summary>A deathrattle that does nothing (it only counts as a trigger).</summary>
[CardEffect("TEST_DR_NOTHING")]
internal sealed class DrNothing : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
    }
}

/// <summary>Friendly deathrattles trigger one extra time.</summary>
[CardEffect("TEST_TITUS")]
internal sealed class TestTitus : CombatEffect
{
    public override int ExtraDeathrattles(Fight fight, Unit self) => 1;
}

/// <summary>Deathrattle: summon a Falling Sky Golem (4/2 Divine Shield).</summary>
[CardEffect("TEST_DR_GOLEM")]
internal sealed class DrSummonFallingSkyGolem : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        fight.SummonAt(self, Tokens.Make("BG35_342", 4, 2, Tribe.Mech, keywords: Keyword.DivineShield));
}

/// <summary>Rally: friendly Dragons get +3 Attack.</summary>
[CardEffect("TEST_RALLY_PUMP_DRAGONS")]
internal sealed class RallyPumpDragons : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        foreach (var dragon in fight.Alive(self.Side, Tribe.Dragon))
        {
            fight.Buff(dragon, 3, 0);
        }
    }
}

/// <summary>Deathrattle: summon a golden C'Thun 2/2 (to see the golden text of the Deity).</summary>
[CardEffect("TEST_DR_GOLDEN_CTHUN")]
internal sealed class DrSummonGoldenCThun : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        fight.SummonAt(self, SimMinion.Create(Tokens.CThunId, 2, 2, Tribe.Aberration, golden: true));
}

/// <summary>Deathrattle: summon a normal Y'Shaarj 1/1 (to see its deathrattle without waking the Deity).</summary>
[CardEffect("TEST_DR_YSHAARJ")]
internal sealed class DrSummonYShaarj : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) =>
        fight.SummonAt(self, SimMinion.Create(Tokens.YShaarjId, 1, 1, Tribe.Aberration, mechanics: CardMechanic.Deathrattle));
}
