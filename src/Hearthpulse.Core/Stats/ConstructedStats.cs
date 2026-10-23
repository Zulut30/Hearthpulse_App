using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Core.Stats;

/// <summary>Header numbers of a Constructed format page.</summary>
/// <param name="OnPlay">Games the player went first; <paramref name="OnCoin"/> the ones on the coin.</param>
public sealed record ConstructedSummary(
    WinLoss Record,
    TimeSpan TimePlayed,
    double? AverageTurns,
    TimeSpan? AverageDuration,
    string? LatestRank,
    string? BestRank,
    WinLoss OnPlay,
    WinLoss OnCoin);

/// <summary>A ranked game on the rank chart: star level at its start (51 = Legend) and the rank's name.</summary>
public sealed record RankPoint(GameRecord Game, int StarLevel, int? LegendRank, string Label);

/// <summary>The player's results with one deck (deck code when known, otherwise its name and class).</summary>
public sealed record DeckStats(string Key, string Name, string PlayerClass, string? DeckCode, WinLoss Record, DateTime LastPlayed);

/// <summary>Results of a group of games, with the community win rate to compare when there is one.</summary>
public sealed record GroupStats(string Key, string Label, WinLoss Record, double? GlobalWinRate = null);

/// <summary>Games that lasted <see cref="MinTurns"/>..<see cref="MaxTurns"/> of the player's turns.</summary>
public sealed record LengthBucket(string Label, int MinTurns, int MaxTurns, WinLoss Record);

/// <summary>Pure aggregates over saved Constructed games for the My Stats page.</summary>
public static class ConstructedStats
{
    private static readonly string[] Leagues = ["Bronze", "Silver", "Gold", "Platinum", "Diamond"];

    private static readonly (string Label, int Min, int Max)[] Lengths =
    [
        ("1–5", 1, 5),
        ("6–7", 6, 7),
        ("8–9", 8, 9),
        ("10–11", 10, 11),
        ("12+", 12, int.MaxValue),
    ];

    public static ConstructedSummary Summary(IReadOnlyList<GameRecord> games)
    {
        var ranks = RankTimeline(games);
        var timed = games.Where(g => g.Duration > TimeSpan.Zero).ToList();
        var best = ranks.OrderByDescending(r => r.StarLevel).ThenBy(r => r.LegendRank ?? int.MaxValue).FirstOrDefault();
        return new ConstructedSummary(
            WinLoss.Of(games),
            StatsFilter.TimePlayed(games),
            games.Count > 0 ? games.Average(g => g.Turns) : null,
            timed.Count > 0 ? TimeSpan.FromTicks((long)timed.Average(g => g.Duration.Ticks)) : null,
            ranks.Count > 0 ? ranks[^1].Label : null,
            best?.Label,
            WinLoss.Of(games.Where(g => g.WentFirst == true)),
            WinLoss.Of(games.Where(g => g.WentFirst == false)));
    }

    /// <summary>
    /// Star level of a game's rank at its start: the saved star level, else parsed from the saved rank text
    /// ("Platinum 5", "Legend 1,204"); null when neither is there.
    /// </summary>
    public static (int StarLevel, int? LegendRank)? RankOf(GameRecord game)
    {
        if (game.RankLegend is > 0)
        {
            return (51, game.RankLegend);
        }

        if (game.RankStarLevel is > 0 and var stars)
        {
            return (Math.Min(stars, 51), null);
        }

        var text = game.Rank?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("Legend", StringComparison.Ordinal))
        {
            var digits = new string(text.Where(char.IsDigit).ToArray());
            return (51, int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var legend) && legend > 0 ? legend : null);
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var league = parts.Length == 2 ? Array.IndexOf(Leagues, parts[0]) : -1;
        if (league < 0 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rank) || rank is < 1 or > 10)
        {
            return null;
        }

        return ((league * 10) + (10 - rank) + 1, null);
    }

    /// <summary>Ranked games with a known rank, oldest first.</summary>
    public static IReadOnlyList<RankPoint> RankTimeline(IReadOnlyList<GameRecord> games) =>
        games.Where(g => g.Mode == "Ranked")
            .OrderBy(g => g.StartedAt)
            .Select(g => (Game: g, Rank: RankOf(g)))
            .Where(x => x.Rank is not null)
            .Select(x => new RankPoint(x.Game, x.Rank!.Value.StarLevel, x.Rank.Value.LegendRank,
                new RankInfo(string.Empty, 0, x.Rank.Value.StarLevel, x.Rank.Value.LegendRank ?? 0, 0).Display))
            .ToList();

    /// <summary>Results per deck, most recently played first.</summary>
    public static IReadOnlyList<DeckStats> Decks(IReadOnlyList<GameRecord> games) =>
        games.Where(g => !string.IsNullOrEmpty(g.DeckName) || !string.IsNullOrEmpty(g.DeckCode))
            .GroupBy(g => g.DeckCode is { Length: > 0 } code ? code : $"{g.DeckName}|{g.PlayerClass}")
            .Select(group =>
            {
                var latest = group.MaxBy(g => g.StartedAt)!;
                return new DeckStats(group.Key, latest.DeckName ?? "Deck", latest.PlayerClass, latest.DeckCode, WinLoss.Of(group), latest.StartedAt);
            })
            .OrderByDescending(d => d.LastPlayed)
            .ToList();

    /// <summary>Results per class played, compared with the class's community win rate when a meta is given.</summary>
    public static IReadOnlyList<GroupStats> ByPlayerClass(IReadOnlyList<GameRecord> games, ConstructedMeta? meta = null) =>
        games.Where(g => g.PlayerClass.Length > 0)
            .GroupBy(g => g.PlayerClass)
            .Select(g => new GroupStats(g.Key, g.Key, WinLoss.Of(g), meta is null ? null : ClassWinRate(meta, g.Key)))
            .OrderByDescending(g => g.Record.Games)
            .ToList();

    /// <summary>Results against each opponent class (no community number: the meta has no class-vs-class table).</summary>
    public static IReadOnlyList<GroupStats> ByOpponentClass(IReadOnlyList<GameRecord> games) =>
        games.Where(g => g.OpponentClass.Length > 0)
            .GroupBy(g => g.OpponentClass)
            .Select(g => new GroupStats(g.Key, g.Key, WinLoss.Of(g)))
            .OrderByDescending(g => g.Record.Games)
            .ToList();

    /// <summary>Results by game length in the player's turns (empty buckets included, so the chart keeps its axis).</summary>
    public static IReadOnlyList<LengthBucket> ByLength(IReadOnlyList<GameRecord> games) =>
        Lengths.Select(l => new LengthBucket(l.Label, l.Min, l.Max, WinLoss.Of(games.Where(g => g.Turns >= l.Min && g.Turns <= l.Max)))).ToList();

    /// <summary>
    /// Results per archetype of the player's decks (decks the meta cannot place are left out), compared with the
    /// archetype's community win rate.
    /// </summary>
    public static IReadOnlyList<GroupStats> ByArchetype(IReadOnlyList<GameRecord> games, Func<GameRecord, MetaArchetype?> archetypeOf)
    {
        // Matching decodes the deck and scores every archetype of its class: once per deck, not once per game.
        var known = new Dictionary<(string? Code, string Class), MetaArchetype?>();
        MetaArchetype? Of(GameRecord game)
        {
            var key = (game.DeckCode, game.PlayerClass);
            if (!known.TryGetValue(key, out var archetype))
            {
                archetype = string.IsNullOrEmpty(game.DeckCode) ? null : archetypeOf(game);
                known[key] = archetype;
            }

            return archetype;
        }

        return games.Select(g => (Game: g, Archetype: Of(g)))
            .Where(x => x.Archetype is not null)
            .GroupBy(x => x.Archetype!.Id)
            .Select(g => new GroupStats(g.First().Archetype!.Key, g.First().Archetype!.Name, WinLoss.Of(g.Select(x => x.Game)), g.First().Archetype!.WinRate))
            .OrderByDescending(g => g.Record.Games)
            .ToList();
    }

    /// <summary>The archetype of a deck code in the meta (by its core cards), or null.</summary>
    public static MetaArchetype? ArchetypeOf(string? deckCode, string? playerClass, ConstructedMeta? meta, CardDb cards)
    {
        if (meta is null || string.IsNullOrEmpty(deckCode) || DeckString.TryDecode(deckCode) is not { } deck)
        {
            return null;
        }

        var ids = deck.Cards.Keys.Select(dbf => cards.Get(dbf)?.Id).OfType<string>().ToList();
        return meta.Match(playerClass, ids);
    }

    /// <summary>A class's community win rate: its archetypes weighted by games.</summary>
    public static double? ClassWinRate(ConstructedMeta meta, string playerClass)
    {
        var archetypes = meta.ForClass(playerClass).Where(a => a.Games > 0).ToList();
        var games = archetypes.Sum(a => (double)a.Games);
        return games <= 0 ? null : archetypes.Sum(a => a.WinRate * a.Games) / games;
    }
}
