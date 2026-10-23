namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Player-level effects created during a fight (e.g. Diremuck Forager's "when you have space" summon, which keeps going
/// after the Forager dies). They become off-board extra units like hero powers and trinkets, so they receive the same
/// side hooks (after a friendly minion dies, ...). Uses the engine's internal extra-unit support from this assembly; no
/// engine file is changed.
/// </summary>
internal static class PlayerEffects
{
    /// <summary>Adds an off-board holder for <paramref name="effect"/> to <paramref name="side"/> and returns it (its Counter/Counter2 are free state).</summary>
    public static Unit Attach(Side side, string cardId, CombatEffect effect)
    {
        var holder = new Unit(SimMinion.Create(cardId, 0, 0), side, [effect], extra: true);
        side.AddExtra(holder);
        return holder;
    }
}
