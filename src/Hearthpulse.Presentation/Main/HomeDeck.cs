using System.Globalization;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>A deck banner on the home page: the deck's name over its class hero's art, "56 % · 41 games" under it.</summary>
public sealed record HomeDeck(string Key, string Name, string HeroCardId, string Line)
{
    /// <summary>The <paramref name="count"/> decks played last, of any format, with games recorded.</summary>
    public static IReadOnlyList<HomeDeck> Latest(IEnumerable<DeckFamily> families, Func<string, string> classHero, int count = 3) =>
        families.Where(f => f.Record.Games > 0)
            .OrderByDescending(f => f.LastPlayed)
            .Take(count)
            .Select(f => new HomeDeck(
                f.Key,
                f.Name,
                classHero(f.PlayerClass),
                (f.Record.WinRate ?? 0).ToString("P0", CultureInfo.CurrentCulture) + " · " + Loc.P("Stats.Games", f.Record.Games)))
            .ToList();
}
