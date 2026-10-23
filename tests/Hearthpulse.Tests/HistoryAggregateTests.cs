using Hearthpulse.Core.Data;

namespace Hearthpulse.Tests;

/// <summary>
/// The counting aggregates run in SQL; they must give what grouping the games in C# gave (kept here as the reference),
/// including the order of groups with the same number of games.
/// </summary>
public sealed class HistoryAggregateTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-aggregates-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public void Sql_aggregates_match_grouping_the_games()
    {
        var store = new HistoryStore(_db);
        var random = new Random(23);
        string[] modes = ["Ranked", "Casual", "Arena", "Battlegrounds", "Duos", "Mercenaries", "Brawl"];
        string[] classes = ["MAGE", "WARRIOR", "ROGUE", "PRIEST", ""];
        string[] results = ["Win", "Loss", "Tie", GameRecord.AbandonedResult];
        string[] codes = ["AAE1", "AAE2", "AAE3"];
        var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 400; i++)
        {
            var mode = modes[random.Next(modes.Length)];
            var result = results[random.Next(results.Length)];
            var battlegrounds = mode is "Battlegrounds" or "Duos";
            store.Add(new GameRecord
            {
                Key = $"game-{i}",
                // Not in insertion order: the C# grouping followed the row order, not the start time.
                StartedAt = start.AddMinutes(random.Next(100_000)),
                EndedAt = start.AddMinutes(100_000 + i),
                Mode = mode,
                PlayerName = "Me",
                HeroCardId = "HERO_0" + random.Next(1, 9),
                PlayerClass = classes[random.Next(classes.Length)],
                OpponentClass = classes[random.Next(classes.Length)],
                Result = result,
                Placement = battlegrounds && result != GameRecord.AbandonedResult ? random.Next(0, 9) : 0,
                DeckName = battlegrounds ? null : "Deck",
                DeckCode = battlegrounds ? null : codes[random.Next(codes.Length)],
            });
        }

        // Every game, in row (id) order, as the old queries read them.
        var all = store.Recent(int.MaxValue).OrderBy(g => g.Id).ToList();
        // Add drops an abandoned game that matches one already saved; most games are kept.
        Assert.True(all.Count > 250, $"{all.Count} games");
        var constructed = all.Where(g => g.Mode is not ("Battlegrounds" or "Duos" or "Mercenaries") && g.Result != GameRecord.AbandonedResult).ToList();

        var classStats = constructed.Where(g => g.PlayerClass != "")
            .GroupBy(g => (g.PlayerClass, g.Mode))
            .Select(g => new ClassAggregate(g.Key.PlayerClass, g.Key.Mode, g.Count(), g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss")))
            .OrderByDescending(c => c.Games)
            .ToList();
        Assert.Equal(classStats, store.ClassStats());
        Assert.Contains(classStats.GroupBy(c => c.Games), g => g.Count() > 1);

        foreach (var code in new string?[] { null, "AAE2", "nope" })
        {
            var matchups = constructed.Where(g => g.OpponentClass != "" && (code is null || g.DeckCode == code))
                .GroupBy(g => g.OpponentClass)
                .Select(g => new MatchupAggregate(g.Key, g.Count(), g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss")))
                .OrderByDescending(m => m.Games)
                .ToList();
            Assert.Equal(matchups, store.MatchupStats(code));
        }

        foreach (var mode in new[] { "Battlegrounds", "Duos", "Ranked" })
        {
            var distribution = new int[8];
            foreach (var game in all.Where(g => g.Mode == mode && g.Placement is >= 1 and <= 8))
            {
                distribution[game.Placement - 1]++;
            }

            Assert.Equal(distribution, store.PlacementDistribution(mode));
        }
    }
}
