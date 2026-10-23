namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

// Dark Paradox (BG36_360): "This has a different Dark Gift, stats, and Tier each game!" In a game it is one of five
// forms, each its own card (BG36_360t3/t4/t5/t6/t9, golden BG36_360_Gt3...): only two of them matter in combat.
// Form t4 = Toreth's Blessing ("Divine Shield takes N hits to break") and form t9 = Golemancy ("Deathrattle: Summon a Golem
// with this minion's stats", golden: double). The other forms (a card is played / Rally gets cards / Battlecries
// triggered) only act in the Tavern. The classes below register those two forms by card id, and the same two Dark Gifts
// by their enchantment ids, in case the log shows the gift as an enchantment on a minion. The untransformed card
// (BG36_360) shows no gift and stays "not simulated".

/// <summary>"This minion's Divine Shield takes {0} hits to break": every hit that would break it lets it take one hit less instead.</summary>
internal abstract class MultiHitShield : CombatEffect
{
    /// <summary>Hits one Divine Shield takes.</summary>
    protected abstract int Hits(Unit self);

    public sealed class State
    {
        /// <summary>Hits left before the current shield breaks; 0 without a shield.</summary>
        public int Remaining;
    }

    /// <summary>A shield with no hit count yet (captured, or given by another effect) starts with all of its hits.</summary>
    public override void Refresh(Fight fight, Unit self)
    {
        var state = UnitState<State>.Of(self);
        if (self.DivineShield && state.Remaining <= 0)
        {
            state.Remaining = Hits(self);
        }
    }

    /// <summary>The engine removes the shield on a hit; hand it back while hits are left.</summary>
    public override void OnFriendlyShieldLost(Fight fight, Unit self, Unit unit)
    {
        if (unit != self)
        {
            return;
        }

        var state = UnitState<State>.Of(self);
        if (state.Remaining <= 0)
        {
            return;
        }

        if (--state.Remaining > 0 && self.IsOnBoard)
        {
            if (fight.Tracing) fight.Trace($"{self} Divine Shield holds ({state.Remaining} hits left)");
            fight.GiveKeywords(self, Keyword.DivineShield);
        }
    }
}

/// <summary>Dark Paradox, Divine Shield form (BG36_360t4): shield takes {0} hits to break, 3 (golden 4; the captured TAG_SCRIPT_DATA_NUM_1).</summary>
[CardEffect("BG36_360t4")]
[CardEffect("BG36_360_Gt4", Golden = GoldenFilter.GoldenOnly)]
internal sealed class DarkParadoxShield : MultiHitShield
{
    protected override int Hits(Unit self) => self.Spec.ScriptData1 > 0 ? self.Spec.ScriptData1 : self.Pick(3, 4);
}

/// <summary>Toreth's Blessing (Dark Gift enchantment BG36_MidGameEffect_000t15e): "This minion's Divine Shield takes 3 hits to break."</summary>
[CardEffect("BG36_MidGameEffect_000t15e")]
internal sealed class TorethsBlessing : MultiHitShield
{
    protected override int Hits(Unit self) => 3;
}

/// <summary>
/// Dark Paradox, Golem form (BG36_360t9): "Deathrattle: Summon a Golem with this minion's stats." Golden (BG36_360_Gt9):
/// "with double this minion's stats". The Golem is a normal Deserted Golem with the Attack and maximum Health the minion had.
/// </summary>
[CardEffect("BG36_360t9")]
[CardEffect("BG36_360_Gt9", Golden = GoldenFilter.GoldenOnly)]
internal sealed class DarkParadoxGolem : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => Golemancy.SummonGolem(fight, self, self.Pick(1, 2));
}

/// <summary>Golemancy (Dark Gift enchantment BG36_MidGameEffect_000t61e): "Deathrattle: Summon a Golem with this minion's stats."</summary>
[CardEffect("BG36_MidGameEffect_000t61e")]
internal sealed class Golemancy : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self) => SummonGolem(fight, self, 1);

    internal static void SummonGolem(Fight fight, Unit self, int factor) =>
        fight.SummonAt(self, Tokens.Make(Tokens.DesertedGolemId, self.Attack * factor, Math.Max(self.MaxHealth, 1) * factor, Tribe.None));
}
