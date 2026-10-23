namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Goldrinn, the Great Wolf (BGS_018): "Deathrattle: Your Beasts have +{0}/+{1} until next turn." (+7/+7; golden
/// +14/+14.)
/// </summary>
/// <remarks>
/// "Until next turn" covers the rest of the fight: living friendly Beasts get it now, Beasts summoned later this fight
/// (tokens, reborn copies) when they arrive. The amount is the captured script data, else the printed value.
/// </remarks>
[CardEffect("BGS_018")]
internal sealed class Goldrinn : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var (attack, health) = DeathKit.Shown(self, (7, 7), (14, 14));
        DeathKit.BuffAll(fight, self.Side, Tribe.Beast, attack, health);
        self.Side.AddSummonBuff(Tribe.Beast, attack, health);
    }
}
