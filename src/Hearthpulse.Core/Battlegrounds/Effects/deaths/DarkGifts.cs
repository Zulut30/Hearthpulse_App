namespace Hearthpulse.Core.Battlegrounds.Effects.Deaths;

// Dark Gifts (BG36_MidGameEffect_000*) that act in combat, by the enchantment the gift leaves on the minion. Which gifts
// matter in a fight: PlayerCardTable. Toreth's Blessing and Golemancy live with Dark Paradox (DarkParadox.cs).

/// <summary>Offensive Sacrifice (BG36_MidGameEffect_000te2): "Deathrattle: Give this minion's Attack to another friendly minion."</summary>
[CardEffect("BG36_MidGameEffect_000te2")]
internal sealed class OffensiveSacrifice : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        if (fight.RandomFriendly(self) is { } target)
        {
            fight.Buff(target, self.Attack, 0);
        }
    }
}

/// <summary>Defensive Sacrifice (BG36_MidGameEffect_000t2e2): "Deathrattle: Give this minion's max Health to another friendly minion."</summary>
[CardEffect("BG36_MidGameEffect_000t2e2")]
internal sealed class DefensiveSacrifice : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        if (fight.RandomFriendly(self) is { } target)
        {
            fight.Buff(target, 0, Math.Max(self.MaxHealth, 0));
        }
    }
}

/// <summary>
/// Persisting Horror (BG36_MidGameEffect_000t12e): "Reborn. Is Reborn with full stats and Bonus Keywords." Instead of a
/// fresh 1-Health copy, the minion comes back with the Attack and maximum Health it had when it died and the keywords it
/// had when the fight started (minus Reborn). Summon buffs and "after you summon" effects then apply on top, as to any
/// summoned minion.
/// </summary>
[CardEffect("BG36_MidGameEffect_000t12e")]
internal sealed class PersistingHorror : CombatEffect
{
    public override SimMinion RebornAs(Fight fight, Unit dead, SimMinion copy) =>
        SimMinion.Create(copy.CardId, dead.Attack, Math.Max(dead.MaxHealth, 1), copy.Tribes,
            (copy.BaseKeywords | dead.Spec.Keywords) & ~Keyword.Reborn, copy.Golden, copy.Mechanics) with
        {
            RebornAttack = copy.RebornAttack,
            BaseHealth = copy.BaseHealth,
            BaseKeywords = copy.BaseKeywords,
            TechLevel = copy.TechLevel,
        };
}
