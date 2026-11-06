using System.Globalization;
using Hearthpulse.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed partial class HistoryStore
{
    public IReadOnlyList<HeroAggregate> HeroStats(Func<string, string> baseHero, string mode = "Battlegrounds")
    {
        var games = Query($"{ListSelect} WHERE mode = $mode AND placement > 0;", p => p.AddWithValue("$mode", mode));
        var top = mode == "Duos" ? 2 : 4;
        return games
            .GroupBy(g => baseHero(g.HeroCardId))
            .Select(g => new HeroAggregate(
                g.Key,
                g.Count(),
                g.Average(x => x.Placement),
                g.Count(x => x.Placement <= top) / (double)g.Count(),
                g.Count(x => x.Placement == 1) / (double)g.Count(),
                g.Any(x => x.RatingDelta is not null) ? g.Sum(x => x.RatingDelta ?? 0) : null))
            .OrderByDescending(h => h.Games).ThenBy(h => h.AveragePlace)
            .ToList();
    }

    // The counting aggregates run in SQL: reading every game (with its board and combat JSON) to count it cost about
    // 150 ms and 20 MB for 20 000 games, several times after each game. Groups with as many games keep the order of
    // their first game (MIN(id)), as grouping the rows in C# did.
    public IReadOnlyList<ClassAggregate> ClassStats() =>
        Aggregate("""
            SELECT player_class, mode, COUNT(*), SUM(result = 'Win'), SUM(result = 'Loss') FROM games
            WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND player_class <> '' AND result <> 'Abandoned'
            GROUP BY player_class, mode ORDER BY COUNT(*) DESC, MIN(id);
            """, null, r => new ClassAggregate(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)));

    /// <summary>Constructed results grouped per deck (deck code when known, otherwise name and class).</summary>
    public IReadOnlyList<DeckAggregate> DeckStats() =>
        Query($"{ListSelect} WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND deck_name IS NOT NULL AND result <> 'Abandoned';", null)
            .GroupBy(DeckRecords.DeckKey)
            .Select(g =>
            {
                var latest = g.MaxBy(x => x.StartedAt)!;
                return new DeckAggregate(latest.DeckName!, latest.DeckCode, latest.PlayerClass, g.Count(),
                    g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss"), latest.StartedAt);
            })
            .OrderByDescending(d => d.LastPlayed)
            .ToList();

    /// <summary>Win rate against each opponent class, optionally for one deck.</summary>
    public IReadOnlyList<MatchupAggregate> MatchupStats(string? deckCode = null) =>
        Aggregate("SELECT opponent_class, COUNT(*), SUM(result = 'Win'), SUM(result = 'Loss') FROM games "
                  + "WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND opponent_class <> '' AND result <> 'Abandoned'"
                  + (deckCode is null ? string.Empty : " AND deck_code = $code")
                  + " GROUP BY opponent_class ORDER BY COUNT(*) DESC, MIN(id);",
            p =>
            {
                if (deckCode is not null) p.AddWithValue("$code", deckCode);
            },
            r => new MatchupAggregate(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));

    public IReadOnlyList<RatingPoint> RatingHistory(string mode = "Battlegrounds", int limit = 300) =>
        Query($"{ListSelect} WHERE mode = $mode AND rating_after IS NOT NULL ORDER BY started_at DESC LIMIT $limit;", p =>
            {
                p.AddWithValue("$mode", mode);
                p.AddWithValue("$limit", limit);
            })
            .OrderBy(g => g.StartedAt)
            .Select(g => new RatingPoint(g.EndedAt, g.RatingAfter!.Value))
            .ToList();

    public int[] PlacementDistribution(string mode = "Battlegrounds")
    {
        var distribution = new int[8];
        foreach (var (placement, games) in Aggregate("SELECT placement, COUNT(*) FROM games WHERE mode = $mode AND placement BETWEEN 1 AND 8 GROUP BY placement;",
                     p => p.AddWithValue("$mode", mode), r => (r.GetInt32(0), r.GetInt32(1))))
        {
            distribution[placement - 1] = games;
        }

        return distribution;
    }

    /// <summary>Runs an aggregate query and maps each row.</summary>
    private List<T> Aggregate<T>(string sql, Action<SqliteParameterCollection>? bind, Func<SqliteDataReader, T> map)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command.Parameters);
            using var reader = command.ExecuteReader();
            var list = new List<T>();
            while (reader.Read())
            {
                list.Add(map(reader));
            }

            return list;
        }
    }
}
