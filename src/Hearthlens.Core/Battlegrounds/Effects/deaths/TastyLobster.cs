namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Tasty Lobster (BG36_202): "Deathrattle: Give a random friendly Beast +{0}/+{1}. Improve your future Tasty Lobsters."
/// (+2/+1; golden +4/+2.)
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The buff is the lobster's captured TAG_SCRIPT_DATA_NUM_1/2 (what its text shows, improvements included).</item>
/// <item>A lobster summoned in combat (Hoarding Hyena's Rally) carries no script data. "Future" lobsters show the
/// player's current value, so it uses the highest value among the friendly lobsters captured on the board (and, for the
/// local player only, in hand), scaled to golden or normal (golden numbers are double); else the printed +2/+1
/// (golden +4/+2).</item>
/// <item>"Improve your future Tasty Lobsters" is not applied within the fight: the card does not print by how much, so
/// lobsters summoned later in the same combat use the captured value.</item>
/// </list>
/// </remarks>
[CardEffect(Tokens.TastyLobsterId)]
internal sealed class TastyLobster : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var (attack, health) = Bonus(self);
        if (fight.RandomFriendly(self, Tribe.Beast, includeSelf: true) is { } beast)
        {
            fight.Buff(beast, attack, health);
        }
    }

    /// <summary>The +Attack/+Health this lobster gives.</summary>
    internal static (int Attack, int Health) Bonus(Unit lobster)
    {
        var spec = lobster.Spec;
        if (spec.ScriptData1 > 0 || spec.ScriptData2 > 0)
        {
            return (spec.ScriptData1, spec.ScriptData2);
        }

        // Best captured value, in golden units (a normal lobster's numbers doubled).
        int bestAttack = 0, bestHealth = 0;
        void Consider(IReadOnlyList<SimMinion> minions)
        {
            foreach (var minion in minions)
            {
                if (minion.CardId != Tokens.TastyLobsterId || (minion.ScriptData1 <= 0 && minion.ScriptData2 <= 0))
                {
                    continue;
                }

                var factor = minion.Golden ? 1 : 2;
                var (attack, health) = (minion.ScriptData1 * factor, minion.ScriptData2 * factor);
                if (attack + health > bestAttack + bestHealth)
                {
                    (bestAttack, bestHealth) = (attack, health);
                }
            }
        }

        Consider(lobster.Side.Info.Minions);
        if (!lobster.Side.IsOpponent)
        {
            Consider(lobster.Side.Info.Hand);
        }

        if (bestAttack + bestHealth > 0)
        {
            return lobster.Golden ? (bestAttack, bestHealth) : (bestAttack / 2, bestHealth / 2);
        }

        return lobster.Golden ? (4, 2) : (2, 1);
    }
}
