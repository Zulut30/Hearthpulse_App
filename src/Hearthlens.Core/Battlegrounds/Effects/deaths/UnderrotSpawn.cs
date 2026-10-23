namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Underrot Spawn (BG36_116): "Deathrattle: Summon an 0/2 Tentacle with Taunt. Give your minions +{0} Attack." (+1).
/// Golden: "Summon two 0/2 Tentacles with Taunt. Give your minions +{0} Attack." (+2). In text order: the Tentacles are
/// summoned first, so they get the Attack too.
/// </summary>
[CardEffect("BG36_116")]
internal sealed class UnderrotSpawn : CombatEffect
{
    public override void Deathrattle(Fight fight, Unit self)
    {
        fight.SummonAt(self, Tokens.AberrantTentacle(), self.Pick(1, 2));
        DeathKit.BuffAll(fight, self.Side, Tribe.All, DeathKit.Shown1(self, 1, 2), 0);
    }
}
