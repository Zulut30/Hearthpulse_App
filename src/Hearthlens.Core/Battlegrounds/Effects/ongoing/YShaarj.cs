namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Y'Shaarj (BGFYM_011), the Deity: "Deathrattle: Summon your first {0} Aberrations that died this combat with their
/// maximum stats (except Deities)." {0} is 2 (golden 4). The Aberrations are the first ones in the order they died, each
/// summoned as a fresh copy of its card (printed keywords) with the Attack it had and its maximum Health. All-type minions
/// are Aberrations. See <see cref="DeityEffect"/> for how it awakens.
/// </summary>
[CardEffect(Tokens.YShaarjId)]
internal sealed class YShaarj : DeityEffect
{
    protected override CardMechanic Mechanics => CardMechanic.Deathrattle;

    public override void Deathrattle(Fight fight, Unit self)
    {
        var count = self.Counter > 0 ? self.Counter : self.Pick(2, 4);
        var copies = new List<SimMinion>(count);
        foreach (var dead in self.Side.DeadThisFight)
        {
            if (copies.Count >= count)
            {
                break;
            }

            if (dead == self || !dead.Is(Tribe.Aberration) || Deity.IsDeity(dead.CardId))
            {
                continue;
            }

            var attack = Math.Max(dead.Attack, 0);
            var health = Math.Max(dead.MaxHealth, 1);
            copies.Add(dead.Spec.RebornCopy() with
            {
                Attack = attack,
                Health = health,
                MaxHealth = health,
                Reborn = (dead.Spec.BaseKeywords & Keyword.Reborn) != 0,
            });
        }

        SummonOrder.SummonAll(fight, self, copies);
    }
}
