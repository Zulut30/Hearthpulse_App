using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>One class in this week's Tavern Brawl by the community's games: win rate and popularity as fractions.</summary>
public sealed record BrawlClassStat(string Class, double WinRate, double Popularity, int Games);

/// <summary>
/// This week's Tavern Brawl by class (<c>docs/brawl.md</c>), from HSGuru's archetypes: <see cref="OtherGames"/> are the
/// games of archetypes whose name names no class.
/// </summary>
public sealed record BrawlMeta(IReadOnlyList<BrawlClassStat> Classes, int TotalGames, int OtherGames)
{
    public static BrawlMeta Empty { get; } = new([], 0, 0);

    public bool HasData => Classes.Count > 0;
}

/// <summary>One HSGuru Brawl archetype as the API sends it: win rate and popularity in percent.</summary>
public sealed record BrawlArchetype(string Name, double WinRate, double Popularity, int Games);

public static class BrawlStats
{
    /// <summary>
    /// Archetypes summed into their classes (<see cref="ArchetypeNames.ClassOf"/>): win rate weighted by games, popularity
    /// added up; the most played class first.
    /// </summary>
    public static BrawlMeta ByClass(IReadOnlyList<BrawlArchetype> archetypes, int totalGames)
    {
        var known = archetypes.Where(a => a.Games > 0).Select(a => (Class: ArchetypeNames.ClassOf(a.Name), Archetype: a)).ToList();
        var classes = known.Where(k => k.Class is not null)
            .GroupBy(k => k.Class!)
            .Select(g =>
            {
                var games = g.Sum(k => k.Archetype.Games);
                var winRate = g.Sum(k => k.Archetype.WinRate * k.Archetype.Games) / games / 100;
                return new BrawlClassStat(g.Key, winRate, g.Sum(k => k.Archetype.Popularity) / 100, games);
            })
            .OrderByDescending(c => c.Games)
            .ToList();
        var other = known.Where(k => k.Class is null).Sum(k => k.Archetype.Games);
        return new BrawlMeta(classes, Math.Max(totalGames, classes.Sum(c => c.Games) + other), other);
    }

    /// <summary>
    /// The games of the Brawl played last (the newest <see cref="GameRecord.ScenarioId"/> among Brawl games), without
    /// abandoned ones; empty when no Brawl game has a scenario yet.
    /// </summary>
    public static IReadOnlyList<GameRecord> LatestBrawl(IReadOnlyList<GameRecord> games)
    {
        var brawls = games.Where(g => g.Mode == "Brawl" && g.ScenarioId is not null && !g.IsAbandoned).ToList();
        if (brawls.MaxBy(g => g.StartedAt) is not { ScenarioId: { } scenario })
        {
            return [];
        }

        return brawls.Where(g => g.ScenarioId == scenario).ToList();
    }
}
