namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// The minions Ghastcoiler can summon: public card data only (the Battlegrounds pool), generated into
/// <c>GhastcoilerPool.g.cs</c> by <c>tools/effects/gen_ghastcoiler_pool.py</c>. Each entry is the normal card with its
/// printed stats and keywords and no script data, so card effects fall back to their printed numbers.
/// </summary>
internal static partial class GhastcoilerPool
{
    /// <summary>The summonable minions (shared, immutable).</summary>
    public static IReadOnlyList<SimMinion> Minions => Generated;

    /// <summary>Index of a card in <see cref="Minions"/>, or -1 (tests script random picks with it).</summary>
    public static int IndexOf(string cardId) => Array.FindIndex(Generated, m => m.CardId == cardId);

    private static SimMinion M(string cardId, int attack, int health, int tier, Tribe tribes, Keyword keywords, CardMechanic mechanics) =>
        SimMinion.Create(cardId, attack, health, tribes, keywords, golden: false, mechanics) with { TechLevel = tier };
}
