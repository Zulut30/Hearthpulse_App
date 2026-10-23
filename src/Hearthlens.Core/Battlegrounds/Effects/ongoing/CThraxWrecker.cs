namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// C'Thrax Wrecker (BGDUO_701, Duos): "Battlecry, Deathrattle, and Rally: Give your team's Deities +4/+4." Golden: "+8/+8".
/// The Battlecry is Tavern-only. The Rally (when it attacks, before damage) and the deathrattle give this player's Deity
/// the stats; the teammate's Deity fights in another combat, so it is not touched. See <see cref="Deity.Give"/>.
/// </summary>
[CardEffect("BGDUO_701")]
internal sealed class CThraxWrecker : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target) => Give(fight, self);

    public override void Deathrattle(Fight fight, Unit self) => Give(fight, self);

    private static void Give(Fight fight, Unit self)
    {
        var amount = self.Pick(4, 8);
        Deity.Give(fight, self.Side, amount, amount);
    }
}
