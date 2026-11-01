using Hearthpulse.Core.Battlegrounds.Effects.Ongoing;

namespace Hearthpulse.Core.Battlegrounds.Effects.Player;

// Hero powers whose text acts in combat after the boards are captured (PlayerCardTable), from their English card text
// (patch 37.0). The hero power is an off-board extra unit of its side, like a trinket.

/// <summary>Sprout It Out! (TB_BaconShop_HP_107): "Give +1/+2 and Taunt to minions you summon during combat."</summary>
[CardEffect("TB_BaconShop_HP_107")]
internal sealed class SproutItOut : CombatEffect
{
    public override void AfterFriendlySummoned(Fight fight, Unit self, Unit summoned)
    {
        fight.Buff(summoned, 1, 2);
        fight.GiveKeywords(summoned, Keyword.Taunt);
    }
}

/// <summary>Glory of Combat (BG20_HERO_100p): "After a friendly minion kills an enemy, give it +1 Attack permanently."</summary>
[CardEffect("BG20_HERO_100p")]
internal sealed class GloryOfCombat : CombatEffect
{
    private sealed class Credited
    {
        public bool Done;
    }

    public override void AfterFriendlyDealtDamage(Fight fight, Unit self, Unit dealer, Unit victim, int amount)
    {
        // Later damage to a minion that is already dying does not kill it again.
        if (victim.IsDying && victim.Killer == dealer && dealer.IsOnBoard && UnitState<Credited>.Of(victim) is { Done: false } credit)
        {
            credit.Done = true;
            fight.Buff(dealer, 1, 0);
        }
    }
}

/// <summary>
/// Stormpike Strength (BG22_HERO_003p, Vanndar): "When you have space in combat, summon a copy of your highest-Health minion.
/// (Unlocks on Turn 7.)" Frostwolf Fervor (BG22_HERO_002p, Drek'Thar): the same with the highest-Attack minion.
/// <para>
/// With space when the fight starts, the copy is already on the captured board. What is left is a full board: the copy
/// comes once a friendly minion has died and left room. One copy per combat (the text names one summon); the copy has the
/// minion's current stats and keywords. Before turn 7 (when the turn is known) the power does nothing.
/// </para>
/// </summary>
[CardEffect("BG22_HERO_003p")]
[CardEffect("BG22_HERO_002p")]
internal sealed class SpaceCopyHeroPower : CombatEffect
{
    public const int UnlockTurn = 7;

    public override void OnFightStart(Fight fight, Unit self)
    {
        var turn = self.Side.Info.Turn;
        // Counter: 1 = a copy is still owed.
        self.Counter = self.Side.IsFull && (turn == 0 || turn >= UnlockTurn) ? 1 : 0;
    }

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        if (self.Counter == 0 || self.Side.IsFull)
        {
            return;
        }

        var byHealth = self.CardId == "BG22_HERO_003p";
        if (fight.Best(fight.Alive(self.Side), u => byHealth ? u.Health : u.Attack) is not { } best)
        {
            return;
        }

        self.Counter = 0;
        fight.Summon(self.Side, best.ToSimMinion() with { EntityId = 0 }, self.Side.IndexOf(best) + 1);
    }
}
