using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>A bar of a deck banner's mana curve: its height in pixels (of <see cref="HomeDeck.CurveHeight"/>) and what it counts.</summary>
public sealed record HomeDeckBar(double Height, string Tip) : IImmutableView;

/// <summary>
/// A deck banner on the home page: its key card's art in a square at its own proportions (the deck's priciest
/// legendary, else its priciest card, never the one an earlier banner shows), the class hero as a small round icon, the
/// deck's name, "56 % · 41 games" and its mana curve.
/// </summary>
public sealed record HomeDeck(string Key, string Name, string HeroCardId, string ArtCardId, string Line, IReadOnlyList<HomeDeckBar> Curve) : IImmutableView
{
    /// <summary>The tallest bar of the curve, in pixels.</summary>
    public const double CurveHeight = 24;

    /// <summary>Costs 0 to 6 and "7+".</summary>
    private const int CurveCosts = 8;

    /// <summary>The <paramref name="count"/> decks played last, of any format, with games recorded.</summary>
    public static IReadOnlyList<HomeDeck> Latest(IEnumerable<DeckFamily> families, Func<string, string> classHero, CardDb cards, int count = 3)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        return families.Where(f => f.Record.Games > 0)
            .OrderByDescending(f => f.LastPlayed)
            .Take(count)
            .Select(f =>
            {
                var deck = f.Main.Cards.Select(c => (Card: cards.Get(c.Key), Copies: c.Value)).Where(c => c.Card is not null).ToList();
                var art = deck.Select(c => c.Card!)
                    .OrderByDescending(c => c.IsLegendary).ThenByDescending(c => c.Cost).ThenBy(c => c.Name, StringComparer.Ordinal)
                    .FirstOrDefault(c => shown.Add(c.Id))?.Id ?? classHero(f.PlayerClass);
                return new HomeDeck(
                    f.Key,
                    f.Name,
                    classHero(f.PlayerClass),
                    art,
                    (f.Record.WinRate ?? 0).ToString("P0", CultureInfo.CurrentCulture) + " · " + Loc.P("Stats.Games", f.Record.Games),
                    CurveOf(deck.Select(c => (c.Card!.Cost, c.Copies))));
            })
            .ToList();
    }

    /// <summary>Cards per cost, 0 to 7+, as bar heights: the fullest cost is <see cref="CurveHeight"/>, an empty one a 2 px stub.</summary>
    internal static IReadOnlyList<HomeDeckBar> CurveOf(IEnumerable<(int Cost, int Copies)> cards)
    {
        var counts = new int[CurveCosts];
        foreach (var (cost, copies) in cards)
        {
            counts[Math.Clamp(cost, 0, CurveCosts - 1)] += copies;
        }

        var most = Math.Max(1, counts.Max());
        return counts.Select((n, cost) => new HomeDeckBar(
                n == 0 ? 2 : Math.Max(4, Math.Round(CurveHeight * n / most)),
                Loc.F("Main.Home.CurveTip", cost == CurveCosts - 1 ? "7+" : cost.ToString(CultureInfo.CurrentCulture), n)))
            .ToList();
    }
}
