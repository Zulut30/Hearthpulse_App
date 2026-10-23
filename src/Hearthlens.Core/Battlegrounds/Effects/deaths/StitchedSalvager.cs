namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Stitched Salvager (BG31_999): "Start of Combat: Destroy the minion to the left. Deathrattle: Summon an exact copy of
/// it. (Except Stitched Salvager.)" Golden: "Destroy adjacent minions. Deathrattle: Summon exact copies of them."
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The Start of Combat destroy resolved before capture. The tracker remembers the minions that left the board inside
/// the Salvager's start-of-combat trigger (<see cref="SimMinion.Remembered"/>, as they were when removed; inferred from
/// the log's block structure, unverified against a real log).</item>
/// <item>The deathrattle summons exact copies of them in that order (stats, keywords, enchantments as remembered), at
/// most one for the normal card and two for the golden one, skipping Stitched Salvagers. Each trigger (Titus) summons
/// them again. A copy whose remembered Health is not positive comes back at its maximum Health.</item>
/// <item>A Salvager without remembered minions (a reborn copy, one summoned in combat, or a capture that saw no destroy)
/// summons nothing, like a Salvager whose Start of Combat found no minion.</item>
/// </list>
/// </remarks>
[CardEffect(Id)]
internal sealed class StitchedSalvager : CombatEffect
{
    private const string Id = "BG31_999";

    public override void Deathrattle(Fight fight, Unit self)
    {
        var copies = self.Spec.Remembered
            .Where(static remembered => remembered.CardId != Id)
            .Take(self.Pick(1, 2))
            .Select(ExactCopy);
        DeathKit.SummonInOrder(fight, self, copies);
    }

    private static SimMinion ExactCopy(SimMinion minion) =>
        minion with
        {
            EntityId = 0,
            HasUnsimulatedEffect = false,
            Health = minion.Health > 0 ? minion.Health : Math.Max(1, minion.MaxHealth),
        };
}
