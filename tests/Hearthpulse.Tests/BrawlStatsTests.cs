using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>
/// Tavern Brawl by class (<c>docs/brawl.md</c>, plan stage 4): HSGuru's archetypes summed into classes, the games of the
/// Brawl played last by scenario, and the scenario kept in History.
/// </summary>
public sealed class BrawlStatsTests
{
    private const string Answer = """
        {"rank":"all","total_games":10000,"items":[
          {"archetype":"Priest","winrate":60.0,"popularity":10.0,"games":1000},
          {"archetype":"C'Thun Priest","winrate":70.0,"popularity":30.0,"games":3000},
          {"archetype":"STD Zoolock","winrate":50.0,"popularity":20.0,"games":2000},
          {"archetype":"Rainbow DK","winrate":64.5,"popularity":5.0,"games":500},
          {"archetype":"Big Tree","winrate":40.0,"popularity":3.0,"games":300},
          {"archetype":"","winrate":40.0,"games":10},
          {"archetype":"Mage","winrate":null,"games":10}
        ]}
        """;

    [Fact]
    public void Archetypes_are_summed_into_classes_weighted_by_games()
    {
        using var doc = JsonDocument.Parse(Answer);
        var meta = HearthpulseData.ParseBrawlMeta(doc.RootElement);

        Assert.Equal(["PRIEST", "WARLOCK", "DEATHKNIGHT"], meta.Classes.Select(c => c.Class));
        var priest = meta.Classes[0];
        Assert.Equal(4000, priest.Games);
        Assert.Equal((0.60 * 1000 + 0.70 * 3000) / 4000, priest.WinRate, 6);
        Assert.Equal(0.40, priest.Popularity, 6);
        Assert.Equal(300, meta.OtherGames);
        Assert.Equal(10000, meta.TotalGames);
    }

    [Fact]
    public void A_broken_answer_reads_as_no_data()
    {
        using var doc = JsonDocument.Parse("""{"items":"nope"}""");
        Assert.False(HearthpulseData.ParseBrawlMeta(doc.RootElement).HasData);
        using var empty = JsonDocument.Parse("null");
        Assert.Same(BrawlMeta.Empty, HearthpulseData.ParseBrawlMeta(empty.RootElement));
    }

    private static GameRecord Brawl(string key, int? scenario, DateTime started, string playerClass, string result = "Win") => new()
    {
        Key = key,
        StartedAt = started,
        EndedAt = started.AddMinutes(8),
        Mode = "Brawl",
        GameType = "GT_TAVERNBRAWL",
        PlayerClass = playerClass,
        OpponentClass = "MAGE",
        Result = result,
        Turns = 7,
        ScenarioId = scenario,
    };

    [Fact]
    public void The_latest_brawl_is_the_scenario_played_last_and_history_keeps_it()
    {
        var store = HistoryStore.InMemory();
        var monday = new DateTime(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(store.Add(Brawl("old-1", 3001, monday, "PRIEST")));
        Assert.True(store.Add(Brawl("old-2", null, monday.AddHours(1), "PRIEST")));
        Assert.True(store.Add(Brawl("new-1", 3002, monday.AddDays(7), "MAGE")));
        Assert.True(store.Add(Brawl("new-2", 3002, monday.AddDays(7).AddHours(1), "MAGE", "Loss")));
        Assert.True(store.Add(Brawl("new-3", 3002, monday.AddDays(7).AddHours(2), "ROGUE", GameRecord.AbandonedResult)));

        var games = store.Recent(1000, "Brawl");
        Assert.Equal(3002, games.First(g => g.Key == "new-1").ScenarioId);
        Assert.Null(games.First(g => g.Key == "old-2").ScenarioId);

        var latest = BrawlStats.LatestBrawl(games);
        Assert.Equal(["new-1", "new-2"], latest.Select(g => g.Key).Order(StringComparer.Ordinal));
        var mage = Assert.Single(ConstructedStats.ByPlayerClass(latest));
        Assert.Equal(new WinLoss(2, 1, 1), mage.Record);
    }

    [Fact]
    public void A_scenario_found_later_fills_the_saved_game()
    {
        var store = HistoryStore.InMemory();
        var started = new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(store.Add(Brawl("g", null, started, "PRIEST")));
        Assert.True(store.FillMissingFields(Brawl("g", 3003, started, "PRIEST")));
        Assert.Equal(3003, Assert.Single(store.Recent(10, "Brawl")).ScenarioId);
        Assert.Empty(BrawlStats.LatestBrawl([]));
    }
}
