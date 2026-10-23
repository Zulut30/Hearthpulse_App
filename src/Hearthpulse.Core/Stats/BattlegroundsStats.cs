using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>A minion of a saved final board.</summary>
public sealed record BgBoardMinion(string CardId, int Attack, int Health, bool Golden);

/// <summary>One saved combat, from the player's side.</summary>
public sealed record BgCombatResult(int Turn, string Result, string OpponentHeroCardId, int DamageTaken, int DamageDealt);

/// <summary>Header numbers of the Battlegrounds page.</summary>
/// <param name="PlacementCounts">Games per final place, index 0 = 1st (8 places solo, 4 teams in Duos).</param>
public sealed record BgSummary(int Games, int? HighestRating, double? AveragePlacement, TimeSpan TimePlayed, IReadOnlyList<int> PlacementCounts, int? NetRating);

/// <summary>A game on the rating chart: the rating after it and its change, when known.</summary>
public sealed record BgRatingPoint(GameRecord Game, int Rating, int? Delta);

/// <summary>Community numbers to compare a hero with ("vs Global"); a part the source lacks is null.</summary>
public sealed record BgHeroGlobal(double AveragePlacement, IReadOnlyList<double>? PlacementShares, IReadOnlyDictionary<int, double>? CombatWinRate, double? PickRate);

/// <summary>The player's results with one hero (skins count as their hero).</summary>
/// <param name="CombatWinRate">Share of combats won per turn, for turns with at least one saved combat.</param>
/// <param name="PickRate">Picked over offered, among games that saved the hero offer; null without such games.</param>
public sealed record BgHeroStats(
    string HeroCardId,
    int Games,
    int? NetRating,
    double AveragePlacement,
    IReadOnlyList<int> PlacementCounts,
    IReadOnlyDictionary<int, double> CombatWinRate,
    double? PickRate,
    int Offered,
    BgHeroGlobal? Global);

public enum BgHeroSort
{
    Games,
    AveragePlacement,
    NetRating,
    PickRate,
}

/// <summary>Pure aggregates over saved Battlegrounds games for the My Stats page.</summary>
public static class BattlegroundsStats
{
    /// <summary>Finished games of one Battlegrounds mode ("Battlegrounds" or "Duos") with a known place.</summary>
    public static bool IsGame(GameRecord game, bool duos) =>
        game.Mode == (duos ? "Duos" : "Battlegrounds") && game.Placement > 0;

    public static BgSummary Summary(IReadOnlyList<GameRecord> games, bool duos)
    {
        var places = new int[duos ? 4 : 8];
        foreach (var game in games)
        {
            if (game.Placement >= 1 && game.Placement <= places.Length)
            {
                places[game.Placement - 1]++;
            }
        }

        var ratings = games.SelectMany(g => new[] { g.RatingBefore, g.RatingAfter }).OfType<int>().Where(r => r > 0).ToList();
        var deltas = games.Select(g => g.RatingDelta).OfType<int>().ToList();
        var placed = games.Where(g => g.Placement > 0).ToList();
        return new BgSummary(
            games.Count,
            ratings.Count > 0 ? ratings.Max() : null,
            placed.Count > 0 ? placed.Average(g => g.Placement) : null,
            StatsFilter.TimePlayed(games),
            places,
            deltas.Count > 0 ? deltas.Sum() : null);
    }

    /// <summary>
    /// Rating after each game, oldest first. A game whose rating after was not read takes the next game's rating
    /// before (nothing else changes the rating in between).
    /// </summary>
    public static IReadOnlyList<BgRatingPoint> RatingTimeline(IReadOnlyList<GameRecord> games)
    {
        var ordered = games.OrderBy(g => g.StartedAt).ToList();
        var points = new List<BgRatingPoint>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var game = ordered[i];
            var after = game.RatingAfter ?? (i + 1 < ordered.Count ? ordered[i + 1].RatingBefore : null);
            if (after is not > 0)
            {
                continue;
            }

            points.Add(new BgRatingPoint(game, after.Value, game.RatingBefore is > 0 and var before ? after.Value - before : null));
        }

        return points;
    }

    public static IReadOnlyList<BgBoardMinion> FinalBoard(GameRecord game)
    {
        if (string.IsNullOrEmpty(game.FinalBoard))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(game.FinalBoard);
            return doc.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : doc.RootElement.EnumerateArray()
                    .Where(m => m.ValueKind == JsonValueKind.Object)
                    .Select(m => new BgBoardMinion(Str(m, "id"), Int(m, "atk"), Int(m, "hp"), m.TryGetProperty("gold", out var g) && g.ValueKind == JsonValueKind.True))
                    .Where(m => m.CardId.Length > 0)
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<BgCombatResult> Combats(GameRecord game)
    {
        if (string.IsNullOrEmpty(game.CombatResults))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(game.CombatResults);
            return doc.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : doc.RootElement.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Object)
                    .Select(c => new BgCombatResult(Int(c, "turn"), Str(c, "result"), Str(c, "opponent"), Int(c, "taken"), Int(c, "dealt")))
                    .Where(c => c.Turn > 0 && c.Result.Length > 0)
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Community numbers for a hero from Firestone/API meta; solo only (no source publishes Duos heroes).</summary>
    public static BgHeroGlobal? GlobalOf(MetaHero? hero) =>
        hero is null || hero.AveragePosition <= 0
            ? null
            : new BgHeroGlobal(hero.AveragePosition, hero.PlacementShares, hero.CombatWinRate, hero.PickRate > 0 ? hero.PickRate : null);

    /// <summary>Results per hero. <paramref name="global"/> looks up community numbers by base hero id (null = none).</summary>
    public static IReadOnlyList<BgHeroStats> Heroes(
        IReadOnlyList<GameRecord> games,
        Func<string, string> baseHero,
        bool duos,
        Func<string, BgHeroGlobal?>? global = null)
    {
        // Pick rate: of the games that saved their hero offer, how often the hero was offered and how often picked.
        var offered = new Dictionary<string, int>(StringComparer.Ordinal);
        var pickedWhenOffered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var game in games.Where(g => !string.IsNullOrEmpty(g.OfferedHeroes)))
        {
            var played = baseHero(game.HeroCardId);
            foreach (var hero in game.OfferedHeroes!.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(baseHero).Distinct(StringComparer.Ordinal))
            {
                offered[hero] = offered.GetValueOrDefault(hero) + 1;
                if (hero == played)
                {
                    pickedWhenOffered[hero] = pickedWhenOffered.GetValueOrDefault(hero) + 1;
                }
            }
        }

        var places = duos ? 4 : 8;
        return games
            .Where(g => g.Placement > 0)
            .GroupBy(g => baseHero(g.HeroCardId))
            .Select(group =>
            {
                var list = group.ToList();
                var counts = new int[places];
                foreach (var game in list.Where(g => g.Placement <= places))
                {
                    counts[game.Placement - 1]++;
                }

                var deltas = list.Select(g => g.RatingDelta).OfType<int>().ToList();
                var fights = list.SelectMany(Combats).Where(c => c.Result is "Win" or "Loss" or "Tie")
                    .GroupBy(c => c.Turn)
                    .OrderBy(t => t.Key)
                    .ToDictionary(t => t.Key, t => t.Count(c => c.Result == "Win") / (double)t.Count());
                var timesOffered = offered.GetValueOrDefault(group.Key);
                return new BgHeroStats(
                    group.Key,
                    list.Count,
                    deltas.Count > 0 ? deltas.Sum() : null,
                    list.Average(g => g.Placement),
                    counts,
                    fights,
                    timesOffered > 0 ? pickedWhenOffered.GetValueOrDefault(group.Key) / (double)timesOffered : null,
                    timesOffered,
                    duos ? null : global?.Invoke(group.Key));
            })
            .ToList();
    }

    /// <summary>Heroes with at least <paramref name="minGames"/> games in the chosen order (best first).</summary>
    public static IReadOnlyList<BgHeroStats> Sort(IEnumerable<BgHeroStats> heroes, BgHeroSort sort, int minGames = 1)
    {
        var shown = heroes.Where(h => h.Games >= minGames);
        return (sort switch
        {
            BgHeroSort.AveragePlacement => shown.OrderBy(h => h.AveragePlacement).ThenByDescending(h => h.Games),
            BgHeroSort.NetRating => shown.OrderByDescending(h => h.NetRating ?? int.MinValue).ThenByDescending(h => h.Games),
            BgHeroSort.PickRate => shown.OrderByDescending(h => h.PickRate ?? -1).ThenByDescending(h => h.Games),
            _ => shown.OrderByDescending(h => h.Games).ThenBy(h => h.AveragePlacement),
        }).ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
