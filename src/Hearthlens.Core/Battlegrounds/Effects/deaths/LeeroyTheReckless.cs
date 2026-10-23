namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Leeroy the Reckless (BG23_318): "Deathrattle: Destroy the minion that killed this." Golden: same text.
/// </summary>
/// <remarks>
/// The killer is the minion whose combat damage, effect damage or destroy effect killed Leeroy (<see cref="Unit.Killer"/>).
/// Nothing happens when there is none (e.g. the deathrattle was triggered without Leeroy dying) or the killer is already
/// dead or dying.
/// </remarks>
[CardEffect("BG23_318")]
internal sealed class LeeroyTheReckless : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        if (self.Killer is { IsAlive: true } killer)
        {
            fight.Destroy(killer, self);
        }
    }
}
