namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Cage Gnawer (BG36_211): "Whenever a friendly Beast attacks, give your Beasts +2/+1." Golden: "+4/+2."
/// "A friendly Beast" includes this minion (it is a Beast); "your Beasts" are the living friendly Beasts, itself included.
/// </summary>
[CardEffect("BG36_211")]
internal sealed class CageGnawer : CombatEffect
{
    public override void OnFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target)
    {
        if (!attacker.Is(Tribe.Beast))
        {
            return;
        }

        foreach (var beast in fight.Alive(self.Side, Tribe.Beast))
        {
            fight.Buff(beast, self.Pick(2, 4), self.Pick(1, 2));
        }
    }
}
