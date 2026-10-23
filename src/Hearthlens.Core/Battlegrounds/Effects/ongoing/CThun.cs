namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// C'Thun (BGFYM_000), the Deity: "After this awakens, give this minion's stats split amongst your other minions."
/// Golden: "give double this minion's stats". When C'Thun is summoned (it awakens), each point of its Attack and then each
/// point of its Health goes, one at a time, to a random other living friendly minion. C'Thun keeps its own stats. With no
/// other minion nothing happens. See <see cref="DeityEffect"/> for how it awakens.
/// </summary>
[CardEffect(Tokens.CThunId)]
internal sealed class CThun : DeityEffect
{
    protected override CardMechanic Mechanics => CardMechanic.None;

    public override void OnSummoned(Fight fight, Unit self)
    {
        var others = fight.Alive(self.Side, Tribe.All, self);
        if (others.Count == 0)
        {
            return;
        }

        var factor = self.Pick(1, 2);
        var attack = new int[others.Count];
        var health = new int[others.Count];
        for (var i = self.Attack * factor; i > 0; i--)
        {
            attack[fight.Random.Next(others.Count)]++;
        }

        for (var i = self.Health * factor; i > 0; i--)
        {
            health[fight.Random.Next(others.Count)]++;
        }

        for (var i = 0; i < others.Count; i++)
        {
            if (attack[i] != 0 || health[i] != 0)
            {
                fight.Buff(others[i], attack[i], health[i]);
            }
        }
    }
}
