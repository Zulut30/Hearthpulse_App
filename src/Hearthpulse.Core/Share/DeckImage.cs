using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Share;

/// <summary>A card of the deck picture: cost, name, rarity and copies, as the deck tracker lists it.</summary>
public sealed record DeckImageCard(string CardId, string Name, int Cost, string Rarity, int Copies)
{
    public bool Legendary => Rarity == "LEGENDARY";
}

/// <summary>
/// What the deck picture shows (proposal C1): the list sorted by cost then name, the mana curve (0 … 7+), the class,
/// the format, the crafting cost in dust and the deck code. Built from the code, so it is the same on the deck page and
/// the match page.
/// </summary>
/// <param name="Curve">Copies per cost, index 0 … 7 (7 = seven and more).</param>
public sealed record DeckImageData(
    string Name,
    string PlayerClass,
    string Format,
    string Code,
    IReadOnlyList<DeckImageCard> Cards,
    IReadOnlyList<int> Curve,
    int Dust)
{
    public int CardCount => Cards.Sum(c => c.Copies);

    /// <summary>The average cost of the cards (copies counted), or 0 for no card.</summary>
    public double AverageCost => CardCount == 0 ? 0 : Cards.Sum(c => c.Cost * c.Copies) / (double)CardCount;
}

/// <summary>Builds <see cref="DeckImageData"/>.</summary>
public static class DeckImage
{
    /// <summary>Bars of the curve: costs 0 … 6 and 7+.</summary>
    public const int CurveBuckets = 8;

    /// <summary>
    /// The picture of a deck code; null when the code does not decode. A card the card data lacks keeps its dbf id as a
    /// name and costs no dust (unknown, not guessed).
    /// </summary>
    public static DeckImageData? From(string? code, string? name, string? playerClass, CardDb cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (string.IsNullOrWhiteSpace(code) || DeckString.TryDecode(code.Trim()) is not { } deck)
        {
            return null;
        }

        var list = deck.Cards
            .Select(entry => (Card: cards.Get(entry.Key), Dbf: entry.Key, Copies: entry.Value))
            .Select(c => new DeckImageCard(
                c.Card?.Id ?? string.Empty,
                c.Card?.Name ?? c.Dbf.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.Card?.Cost ?? 0,
                c.Card?.Rarity ?? string.Empty,
                c.Copies))
            .OrderBy(c => c.Cost)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
        var curve = new int[CurveBuckets];
        foreach (var card in list)
        {
            curve[Math.Clamp(card.Cost, 0, CurveBuckets - 1)] += card.Copies;
        }

        var heroClass = !string.IsNullOrEmpty(playerClass)
            ? playerClass
            : deck.HeroDbfIds.Select(id => cards.Get(id)?.CardClass).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? string.Empty;
        return new DeckImageData(
            string.IsNullOrWhiteSpace(name) ? string.Empty : Stats.DeckName.Display(name.Trim()),
            heroClass,
            deck.Format,
            code.Trim(),
            list,
            curve,
            deck.Cards.Sum(entry => DustOf(cards.Get(entry.Key)) * entry.Value));
    }

    /// <summary>Crafting cost of one copy: 40 / 100 / 400 / 1600 by rarity; free and core cards cost nothing.</summary>
    public static int DustOf(Card? card) => card?.Set is "CORE" or "LEGACY" or "VANILLA" ? 0 : card?.Rarity switch
    {
        "COMMON" => 40,
        "RARE" => 100,
        "EPIC" => 400,
        "LEGENDARY" => 1600,
        _ => 0,
    };
}
