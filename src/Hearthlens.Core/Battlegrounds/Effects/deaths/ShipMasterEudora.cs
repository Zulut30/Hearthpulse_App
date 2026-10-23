namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Ship Master Eudora (BG33_828): "Deathrattle: Give your minions +{0}/+{1}. Golden ones keep it permanently." (+6/+6.)
/// Golden: "Give your minions +{0}/+{1} twice." Two separate buffs of +6/+6, not one of +12/+12. Whether the buff is
/// kept after combat does not matter inside the fight.
/// </summary>
[CardEffect("BG33_828")]
internal sealed class ShipMasterEudora : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var (attack, health) = DeathKit.Shown(self, (6, 6), (6, 6));
        for (var i = self.Pick(1, 2); i > 0; i--)
        {
            DeathKit.BuffAll(fight, self.Side, Tribe.All, attack, health);
        }
    }
}
