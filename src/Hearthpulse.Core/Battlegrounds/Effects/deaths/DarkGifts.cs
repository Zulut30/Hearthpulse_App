using Hearthpulse.Core.Battlegrounds.Effects.Ongoing;

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
/// Persisting Horror (BG36_MidGameEffect_000t12e): "Reborn. Is Reborn with full stats and Bonus Keywords." The engine brings
/// the minion back as a fresh 1-Health copy; a player-level helper then gives the copy the Attack and maximum Health the
/// minion had when it died and the keywords it had when the fight started (minus Reborn).
/// </summary>
[CardEffect(Id)]
internal sealed class PersistingHorror : CombatEffect
{
    public const string Id = "BG36_MidGameEffect_000t12e";

    public override void OnFightStart(Fight fight, Unit self)
    {
        // One helper per side serves every minion with the gift.
        if (!self.Side.Extras.Any(e => e.CardId == Id))
        {
            PlayerEffects.Attach(self.Side, Id, FullReborn.Instance);
        }
    }

    /// <summary>The helper: restores a reborn copy of a minion that carried the gift.</summary>
    private sealed class FullReborn : CombatEffect
    {
        public static readonly FullReborn Instance = new();

        public override void AfterFriendlyReborn(Fight fight, Unit self, Unit reborn)
        {
            if (reborn.RebornFrom is not { } from || !from.Spec.Enchantments.Contains(Id))
            {
                return;
            }

            fight.SetStats(reborn, from.Attack, Math.Max(from.MaxHealth, 1));
            fight.GiveKeywords(reborn, from.Spec.Keywords & ~Keyword.Reborn);
        }
    }
}
