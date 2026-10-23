namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Turquoise Skitterer (BG31_809): "Deathrattle: Your Beetles have +5/+5 this game. Summon a {0}/{1} Beetle."
/// Golden: "+10/+10 ... Summon two {0}/{1} Beetles." In text order: the bonus first (living Beetles and Beetles summoned
/// later this fight, including the ones this summons), then the summon.
/// </summary>
[CardEffect("BG31_809")]
internal sealed class TurquoiseSkitterer : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var bonus = self.Pick(5, 10);
        Beetles.Improve(fight, self.Side, bonus, bonus);
        Beetles.Summon(fight, self, self.Pick(1, 2));
    }
}
