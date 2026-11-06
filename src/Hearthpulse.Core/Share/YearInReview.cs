using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Share;

/// <summary>
/// "Hearthpulse 2026" year in review (proposal A3): the player's own year from local history. Only the player's games and
/// what they played; no opponent names. A field with no data is null (or 0 for counts).
/// </summary>
/// <param name="FavouriteClass">The class with the most non-Battlegrounds games.</param>
/// <param name="FavouriteDeck">The deck name with the most games.</param>
/// <param name="BestRank">The highest ranked rank at a game's start (Legend: the best place).</param>
/// <param name="BestRating">The highest Battlegrounds solo rating after a game.</param>
/// <param name="TopCard">The own deck card played most (card id) with its count.</param>
/// <param name="FavouriteHero">The Battlegrounds base hero picked most, with its games.</param>
/// <param name="TopRate">Battlegrounds solo games in the top 4, over placed games.</param>
public sealed record YearInReviewData(
    int Year,
    int Games,
    TimeSpan TimePlayed,
    WinLoss Record,
    string? FavouriteClass,
    int FavouriteClassGames,
    string? FavouriteDeck,
    string? FavouriteDeckClass,
    int FavouriteDeckGames,
    int BestWinStreak,
    LadderRank? BestRank,
    int? BestRating,
    string? TopCard,
    int TopCardPlays,
    string? FavouriteHero,
    int FavouriteHeroGames,
    int BattlegroundsGames,
    double? TopRate,
    int? BestMonth)
{
    public bool IsEmpty => Games == 0;
}

/// <summary>Builds <see cref="YearInReviewData"/>; pure except for the date rule.</summary>
public static class YearInReview
{
    /// <summary>Environment switch that shows the page any day (tests, renders): <c>HEARTHPULSE_YEAR_REVIEW=1</c>.</summary>
    public const string DevFlag = "HEARTHPULSE_YEAR_REVIEW";

    /// <summary>
    /// The year whose review the app shows on <paramref name="today"/>: the current year from December 1, last year's
    /// in January; null the rest of the year (unless <paramref name="force"/>, then the year of the latest December).
    /// </summary>
    public static int? ShownYear(DateTime today, bool force = false) => today.Month switch
    {
        12 => today.Year,
        1 => today.Year - 1,
        _ => force ? today.Year : null,
    };

    /// <summary>True when the dev flag is set to 1 / true.</summary>
    public static bool Forced(Func<string, string?>? environment = null)
    {
        var value = (environment ?? Environment.GetEnvironmentVariable)(DevFlag);
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="games">Games of any time, any order; only <paramref name="year"/>'s are counted.</param>
    /// <param name="playedCards">Own cards played that year by card id (<see cref="HistoryStore.PlayedCardCounts"/>).</param>
    /// <param name="baseHero">Maps a Battlegrounds hero skin to its hero; identity when null.</param>
    public static YearInReviewData Build(
        IEnumerable<GameRecord> games,
        int year,
        IReadOnlyDictionary<string, int>? playedCards = null,
        Func<string, string>? baseHero = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var all = games.Where(g => g.StartedAt.Year == year && !g.IsAbandoned).OrderBy(g => g.StartedAt).ToList();
        var constructed = all.Where(g => !g.IsBattlegrounds && g.Mode != "Mercenaries").ToList();
        var bgSolo = all.Where(g => g.Mode == "Battlegrounds").ToList();
        var hero = baseHero ?? (h => h);

        var favouriteClass = Top(constructed.Where(g => !string.IsNullOrEmpty(g.PlayerClass) && g.PlayerClass != "NEUTRAL"), g => g.PlayerClass);
        var favouriteDeck = constructed.Where(g => !string.IsNullOrWhiteSpace(g.DeckName))
            .GroupBy(g => (Name: g.DeckName!.Trim(), g.PlayerClass))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Name, StringComparer.Ordinal)
            .Select(g => ((string Name, string Class, int Games)?)(g.Key.Name, g.Key.PlayerClass, g.Count()))
            .FirstOrDefault();
        var bestRank = all.Where(g => g.Mode == "Ranked")
            .Select(ConstructedStats.RankOf)
            .OfType<(int StarLevel, int? LegendRank)>()
            .OrderByDescending(r => r.StarLevel)
            .ThenBy(r => r.LegendRank ?? int.MaxValue)
            .Select(r => new LadderRank(r.StarLevel, r.LegendRank))
            .FirstOrDefault();
        var topCard = playedCards?.Where(c => c.Value > 0)
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => (KeyValuePair<string, int>?)c)
            .FirstOrDefault();
        var favouriteHero = Top(bgSolo.Where(g => !string.IsNullOrEmpty(g.HeroCardId)), g => hero(g.HeroCardId));
        var placed = bgSolo.Where(g => g.Placement > 0).ToList();
        var bestMonth = all.GroupBy(g => g.StartedAt.Month).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (int?)g.Key).FirstOrDefault();

        return new YearInReviewData(
            year,
            all.Count,
            TimeSpan.FromTicks(all.Sum(g => Math.Max(0, Math.Min(g.Duration.Ticks, TimeSpan.FromHours(3).Ticks)))),
            WinLoss.Of(constructed),
            favouriteClass?.Key,
            favouriteClass?.Count ?? 0,
            favouriteDeck is { } d ? DeckName.Display(d.Name) : null,
            favouriteDeck?.Class,
            favouriteDeck?.Games ?? 0,
            SessionRecap.LongestWinStreak(constructed),
            bestRank,
            bgSolo.Select(g => g.RatingAfter).Where(r => r is > 0).Max(),
            topCard?.Key,
            topCard?.Value ?? 0,
            favouriteHero?.Key,
            favouriteHero?.Count ?? 0,
            bgSolo.Count,
            placed.Count == 0 ? null : placed.Count(g => g.Placement <= 4) / (double)placed.Count,
            bestMonth);
    }

    private static (string Key, int Count)? Top(IEnumerable<GameRecord> games, Func<GameRecord, string> key) =>
        games.GroupBy(key, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ((string, int)?)(g.Key, g.Count()))
            .FirstOrDefault();
}
