namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Plaguerunner (BG34_690): "Deathrattle: Your Undead have +{0} Attack this game, wherever they are. (+{1} if triggered
/// outside combat!)" (+2 in combat; golden +4.)
/// </summary>
/// <remarks>
/// In combat: living friendly Undead get the Attack now, and Undead summoned later this fight (tokens, reborn copies)
/// get it when they arrive. The amount is the captured TAG_SCRIPT_DATA_NUM_1 ({0}), else the printed value.
/// </remarks>
[CardEffect("BG34_690")]
internal sealed class Plaguerunner : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        var attack = DeathKit.Shown1(self, 2, 4);
        DeathKit.BuffAll(fight, self.Side, Tribe.Undead, attack, 0);
        self.Side.AddSummonBuff(Tribe.Undead, attack, 0);
    }
}
