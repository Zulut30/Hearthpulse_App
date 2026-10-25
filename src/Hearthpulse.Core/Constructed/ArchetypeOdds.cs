using System.Runtime.CompilerServices;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Constructed;

/// <summary>How likely the opponent plays one archetype, given the cards it has shown.</summary>
/// <param name="Key">The English archetype name (for the translated label).</param>
public sealed record ArchetypeChance(string Key, string Name, double Chance, double WinRate, int Games);

/// <summary>A card the archetype's lists usually run that the opponent has not shown yet.</summary>
public sealed record LikelyCard(string CardId, string Name, int Cost, double Chance);

/// <summary>The opponent's likely archetype while the game runs; <see cref="Archetypes"/> is most likely first.</summary>
public sealed record OpponentArchetypeView(IReadOnlyList<ArchetypeChance> Archetypes, IReadOnlyList<LikelyCard> Cards, int SeenCards, string Source)
{
    public ArchetypeChance Best => Archetypes[0];
}

/// <summary>
/// Weighs the archetypes of the opponent's class by the starting-deck cards it played face up. Each archetype's lists
/// (by games) give how often it runs a card; a shown card multiplies the archetype's share of the meta by that rate
/// (never below <see cref="Floor"/>, so a tech card does not rule an archetype out). Cards the opponent has not shown
/// say nothing: they may still be in the deck or the hand. Only public meta and face-up cards go in.
/// </summary>
public static class ArchetypeOdds
{
    /// <summary>The rate a card no list of the archetype runs still gets.</summary>
    public const double Floor = 0.03;

    /// <summary>A card shown in "what else" needs at least this chance.</summary>
    public const double MinCardChance = 0.3;

    public const int MaxAlternatives = 3;

    public const int MaxCards = 8;

    /// <summary>An alternative needs at least this chance to be listed.</summary>
    private const double MinAlternative = 0.03;

    /// <summary>A card of an archetype without lists, when only its core cards are known.</summary>
    private const double CoreRate = 0.9;

    /// <summary>
    /// Games where the opponent plays a ladder deck: ranked and casual. Arena, Tavern Brawl, games against the AI and
    /// friendly games have their own decks, so a meta archetype would be wrong there.
    /// </summary>
    public static readonly IReadOnlySet<string> LadderGameTypes = new HashSet<string>(StringComparer.Ordinal) { "GT_RANKED", "GT_CASUAL" };

    private static readonly ConditionalWeakTable<ConstructedMeta, Dictionary<int, IReadOnlyDictionary<string, double>>> Rates = new();

    /// <summary>
    /// The view, or null outside a ranked or casual game (<see cref="LadderGameTypes"/>), without a meta of the game's
    /// format, archetypes of the class or a shown card.
    /// </summary>
    public static OpponentArchetypeView? Build(ConstructedMeta? meta, string? gameType, string? formatType, string? heroClass, IReadOnlyCollection<string> shown, CardDb cards)
    {
        if (meta is null || shown.Count == 0 || gameType is null || !LadderGameTypes.Contains(gameType) || !Stats.ArchetypeGuess.FitsFormat(meta, formatType))
        {
            return null;
        }

        var archetypes = meta.ForClass(heroClass).Where(a => a.Games > 0).ToList();
        if (archetypes.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(shown, StringComparer.Ordinal);
        var rates = archetypes.Select(a => RatesOf(meta, a, cards)).ToList();
        var scores = archetypes.Select((a, i) => Math.Log(a.Games) + seen.Sum(c => Math.Log(Math.Max(Floor, rates[i].GetValueOrDefault(c))))).ToList();
        var top = scores.Max();
        var weights = scores.Select(s => Math.Exp(s - top)).ToList();
        var total = weights.Sum();
        var chances = weights.Select(w => w / total).ToList();

        var ranked = archetypes.Select((a, i) => new ArchetypeChance(a.Key, a.Name, chances[i], a.WinRate, a.Games))
            .OrderByDescending(a => a.Chance).ThenByDescending(a => a.Games)
            .ToList();
        var listed = ranked.Take(1).Concat(ranked.Skip(1).Where(a => a.Chance >= MinAlternative).Take(MaxAlternatives)).ToList();

        var likely = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < archetypes.Count; i++)
        {
            foreach (var (card, rate) in rates[i])
            {
                if (!seen.Contains(card))
                {
                    likely[card] = likely.GetValueOrDefault(card) + (chances[i] * rate);
                }
            }
        }

        var shownCards = likely.Where(c => c.Value >= MinCardChance)
            .Select(c => (Card: cards.Get(c.Key), Chance: Math.Min(1, c.Value)))
            .Where(c => c.Card is not null)
            .OrderByDescending(c => c.Chance).ThenBy(c => c.Card!.Cost).ThenBy(c => c.Card!.Name, StringComparer.CurrentCulture)
            .Take(MaxCards)
            .Select(c => new LikelyCard(c.Card!.Id, c.Card.Name, c.Card.Cost, c.Chance))
            .OrderBy(c => c.Cost).ThenByDescending(c => c.Chance)
            .ToList();

        return new OpponentArchetypeView(listed, shownCards, seen.Count, meta.Source);
    }

    /// <summary>
    /// Share of the archetype's lists (weighted by games) that run each card; core cards only when it has no lists.
    /// Decoding the lists is cached per meta.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> RatesOf(ConstructedMeta meta, MetaArchetype archetype, CardDb cards)
    {
        var byArchetype = Rates.GetValue(meta, _ => new Dictionary<int, IReadOnlyDictionary<string, double>>());
        lock (byArchetype)
        {
            if (byArchetype.TryGetValue(archetype.Id, out var cached))
            {
                return cached;
            }

            var lists = meta.Decks.Where(d => d.ArchetypeId == archetype.Id)
                .Select(d => (Weight: Math.Max(1, d.Games), Deck: DeckString.TryDecode(d.DeckString)))
                .Where(d => d.Deck is not null)
                .ToList();
            IReadOnlyDictionary<string, double> rates;
            if (lists.Count > 0)
            {
                double total = lists.Sum(l => l.Weight);
                rates = lists.SelectMany(l => l.Deck!.Cards.Keys.Select(dbf => (Id: cards.Get(dbf)?.Id, l.Weight)))
                    .Where(c => c.Id is not null)
                    .GroupBy(c => c.Id!, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Sum(c => c.Weight) / total, StringComparer.Ordinal);
            }
            else
            {
                rates = archetype.CoreCards.Distinct(StringComparer.Ordinal).ToDictionary(c => c, _ => CoreRate, StringComparer.Ordinal);
            }

            byArchetype[archetype.Id] = rates;
            return rates;
        }
    }
}
