namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// The minions "a random Undead" can be (Deathly Striker): public card data only (the Battlegrounds pool), generated into
/// <c>UndeadPool.g.cs</c> by <c>tools/effects/gen_undead_pool.py</c>. Each entry is the normal card with its printed stats
/// and keywords and no script data, so card effects fall back to their printed numbers.
/// </summary>
internal static partial class UndeadPool
{
    /// <summary>The Undead (shared, immutable).</summary>
    public static IReadOnlyList<SimMinion> Minions => Generated;

    /// <summary>Index of a card among the Undead a player of <paramref name="tavernTier"/> can get, or -1 (tests script picks with it).</summary>
    public static int IndexOf(string cardId, int tavernTier = 0) => Candidates(tavernTier).FindIndex(m => m.CardId == cardId);

    /// <summary>
    /// A random Undead up to the player's Tavern tier (every tier when the tier is unknown), as a fresh copy so each card
    /// is its own instance in the hand.
    /// </summary>
    public static SimMinion? Random(Fight fight, int tavernTier)
    {
        var candidates = Candidates(tavernTier);
        return candidates.Count == 0 ? null : candidates[fight.Random.Next(candidates.Count)] with { };
    }

    private static List<SimMinion> Candidates(int tavernTier) =>
        tavernTier > 0 ? Generated.Where(m => m.TechLevel <= tavernTier).ToList() : Generated.ToList();

    private static SimMinion M(string cardId, int attack, int health, int tier, Tribe tribes, Keyword keywords, CardMechanic mechanics) =>
        SimMinion.Create(cardId, attack, health, tribes, keywords, golden: false, mechanics) with { TechLevel = tier };
}
