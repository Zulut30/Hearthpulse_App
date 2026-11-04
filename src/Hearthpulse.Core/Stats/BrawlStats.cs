using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>One class in this week's Tavern Brawl by the community's games: win rate and popularity as fractions.</summary>
public sealed record BrawlClassStat(string Class, double WinRate, double Popularity, int Games);

/// <summary>
/// This week's Tavern Brawl by class (<c>docs/brawl.md</c>), from HSGuru's archetypes: <see cref="OtherGames"/> are the
/// games of archetypes whose name names no class, or that came without a name or a win rate.
/// </summary>
public sealed record BrawlMeta(IReadOnlyList<BrawlClassStat> Classes, int TotalGames, int OtherGames)
{
    public static BrawlMeta Empty { get; } = new([], 0, 0);

    /// <summary>When the numbers were fetched from the server (the cache file's time); null when not known.</summary>
    public DateTime? FetchedAt { get; init; }

    public bool HasData => Classes.Count > 0;
}

/// <summary>One HSGuru Brawl archetype as the API sends it: win rate and popularity in percent (no win rate: null).</summary>
public sealed record BrawlArchetype(string Name, double? WinRate, double Popularity, int Games);

public static class BrawlStats
{
    /// <summary>
    /// Archetypes summed into their classes (<see cref="ArchetypeNames.ClassOf"/>): win rate weighted by games, popularity
    /// added up; the most played class first. An archetype without a name, a class or a win rate counts as other games.
    /// </summary>
    public static BrawlMeta ByClass(IReadOnlyList<BrawlArchetype> archetypes, int totalGames)
    {
        var known = archetypes.Where(a => a.Games > 0)
            .Select(a => (Class: a.WinRate is null || a.Name.Length == 0 ? null : ArchetypeNames.ClassOf(a.Name), Archetype: a))
            .ToList();
        var classes = known.Where(k => k.Class is not null)
            .GroupBy(k => k.Class!)
            .Select(g =>
            {
                var games = g.Sum(k => k.Archetype.Games);
                var winRate = g.Sum(k => k.Archetype.WinRate!.Value * k.Archetype.Games) / games / 100;
                return new BrawlClassStat(g.Key, winRate, g.Sum(k => k.Archetype.Popularity) / 100, games);
            })
            .OrderByDescending(c => c.Games)
            .ToList();
        var other = known.Where(k => k.Class is null).Sum(k => k.Archetype.Games);
        return new BrawlMeta(classes, Math.Max(totalGames, classes.Sum(c => c.Games) + other), other);
    }

    /// <summary>
    /// The games of the Tavern Brawl played last (<c>GT_TAVERNBRAWL</c>; not friendly or other Brawl modes), without
    /// abandoned ones: the newest game's scenario and the unbroken run of games before it with the same scenario. A Brawl
    /// that comes back weeks later under the same <see cref="GameRecord.ScenarioId"/> starts a run of its own. Empty when
    /// no such game has a scenario yet.
    /// </summary>
    public static IReadOnlyList<GameRecord> LatestBrawl(IReadOnlyList<GameRecord> games)
    {
        var brawls = games.Where(g => g.Mode == "Brawl" && g.GameType == "GT_TAVERNBRAWL" && !g.IsAbandoned)
            .OrderByDescending(g => g.StartedAt)
            .ToList();
        if (brawls.FirstOrDefault() is not { ScenarioId: { } scenario })
        {
            return [];
        }

        return brawls.TakeWhile(g => g.ScenarioId == scenario).ToList();
    }
}
