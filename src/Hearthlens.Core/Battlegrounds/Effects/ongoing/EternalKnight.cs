namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Eternal Knight (BG25_008): "Has +4/+2 for each friendly Eternal Knight that died this game (wherever this is)."
/// Golden: "+8/+4". When a friendly Eternal Knight (normal or golden) dies in combat, every living friendly Eternal Knight
/// gains its bonus once. An Eternal Knight summoned in combat (Eternal Summoner, Eternal Tycoon, a reborn copy) comes
/// with the bonus for every Eternal Knight that died this game, itself included for a reborn copy. See
/// <see cref="CountingAura"/> for how deaths before the fight are inferred.
/// </summary>
[CardEffect(Tokens.EternalKnightId)]
internal sealed class EternalKnight : CountingAura
{
    protected override string CardId => Tokens.EternalKnightId;

    protected override (int Attack, int Health) Printed => (4, 2);

    protected override (int Attack, int Health) PerEvent => (4, 2);

    protected override int EventsThisFight(Fight fight, Side side) => DeadThisFight(side, Tokens.EternalKnightId);

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        if (dead.CardId == Tokens.EternalKnightId)
        {
            CatchUp(fight, self);
        }
    }

    /// <summary>Friendly minions of <paramref name="cardId"/> that died this fight (reborn copies count as their own death).</summary>
    internal static int DeadThisFight(Side side, string cardId)
    {
        var count = 0;
        foreach (var unit in side.DeadThisFight)
        {
            if (unit.CardId == cardId)
            {
                count++;
            }
        }

        return count;
    }
}
