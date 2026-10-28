using System.Text.Json;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>Aggregates behind the My Stats pages (Battlegrounds, Standard/Wild, Arena).</summary>
public sealed class MyStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 18, 0, 0);
    private static int _key;

    private static GameRecord Bg(int place, int? before, int? after, string hero = "TB_BaconShop_HERO_72", DateTime? at = null,
        string mode = "Battlegrounds", string? offered = null, string? combats = null, string? board = null)
    {
        var start = at ?? Now.AddHours(-1);
        return new GameRecord
        {
            Key = $"bg{++_key}",
            StartedAt = start,
            EndedAt = start.AddMinutes(20),
            Mode = mode,
            HeroCardId = hero,
            Placement = place,
            RatingBefore = before,
            RatingAfter = after,
            OfferedHeroes = offered,
            CombatResults = combats,
            FinalBoard = board,
        };
    }

    private static GameRecord Ranked(string result, int turns = 8, bool? first = null, string? rank = null, int? stars = null,
        string cls = "MAGE", string opp = "PRIEST", DateTime? at = null, string mode = "Ranked", string? deck = null, long? deckId = null)
    {
        var start = at ?? Now.AddHours(-1);
        return new GameRecord
        {
            Key = $"c{++_key}",
            StartedAt = start,
            EndedAt = start.AddMinutes(10),
            Mode = mode,
            Format = "FT_STANDARD",
            Result = result,
            Turns = turns,
            WentFirst = first,
            Rank = rank,
            RankStarLevel = stars,
            PlayerClass = cls,
            OpponentClass = opp,
            DeckName = deck,
            DeckId = deckId,
        };
    }

    [Fact]
    public void Periods_keep_games_from_their_start_oldest_first()
    {
        var games = new[]
        {
            Ranked("Win", at: Now.AddDays(-40)),
            Ranked("Win", at: Now.AddDays(-25)),
            Ranked("Loss", at: Now.AddDays(-3)),
            Ranked("Win", at: new DateTime(2026, 10, 1, 0, 30, 0)),
        };

        Assert.Equal(4, StatsFilter.Apply(games, StatsPeriod.All, Now).Count);
        Assert.Single(StatsFilter.Apply(games, StatsPeriod.Week, Now));
        Assert.Equal(3, StatsFilter.Apply(games, StatsPeriod.Month, Now).Count);
        var season = StatsFilter.Apply(games, StatsPeriod.Season, Now);
        Assert.Equal(2, season.Count);
        Assert.True(season[0].StartedAt < season[1].StartedAt);
    }

    [Fact]
    public void Battlegrounds_summary_counts_places_rating_and_time()
    {
        var games = new[] { Bg(1, 6000, 6080), Bg(4, 6080, 6075), Bg(8, 6075, null), Bg(4, null, null) };

        var summary = BattlegroundsStats.Summary(games, duos: false);

        Assert.Equal(4, summary.Games);
        Assert.Equal(6080, summary.HighestRating);
        Assert.Equal(4.25, summary.AveragePlacement);
        Assert.Equal(TimeSpan.FromMinutes(80), summary.TimePlayed);
        Assert.Equal(new[] { 1, 0, 0, 2, 0, 0, 0, 1 }, summary.PlacementCounts);
        Assert.Equal(75, summary.NetRating);
        Assert.Equal(4, BattlegroundsStats.Summary([Bg(2, null, null, mode: "Duos")], duos: true).PlacementCounts.Count);
    }

    [Fact]
    public void Summary_of_no_games_has_no_averages()
    {
        var summary = BattlegroundsStats.Summary([], duos: false);
        Assert.Null(summary.HighestRating);
        Assert.Null(summary.AveragePlacement);
        Assert.Null(summary.NetRating);
    }

    [Fact]
    public void Rating_timeline_takes_a_missing_rating_after_from_the_next_game()
    {
        var games = new[]
        {
            Bg(3, 6000, 6040, at: Now.AddHours(-3)),
            Bg(7, 6040, null, at: Now.AddHours(-2)),
            Bg(1, 6010, 6090, at: Now.AddHours(-1)),
            Bg(5, null, null, at: Now),
        };

        var points = BattlegroundsStats.RatingTimeline(games);

        Assert.Equal(new[] { 6040, 6010, 6090 }, points.Select(p => p.Rating));
        Assert.Equal(new int?[] { 40, -30, 80 }, points.Select(p => p.Delta));
    }

    [Fact]
    public void Final_board_and_combats_read_back_from_the_saved_json()
    {
        var board = JsonSerializer.Serialize(new[] { new { id = "BG28_300", atk = 12, hp = 9, gold = true }, new { id = "BG21_008", atk = 3, hp = 2, gold = false } });
        var game = Bg(2, null, null, board: board, combats: """[{"turn":2,"result":"Win","opponent":"TB_BaconShop_HERO_08","taken":0,"dealt":4}]""");

        var minions = BattlegroundsStats.FinalBoard(game);
        Assert.Equal(2, minions.Count);
        Assert.Equal(new BgBoardMinion("BG28_300", 12, 9, true), minions[0]);
        Assert.False(minions[1].Golden);
        Assert.Equal(new BgCombatResult(2, "Win", "TB_BaconShop_HERO_08", 0, 4), Assert.Single(BattlegroundsStats.Combats(game)));

        Assert.Empty(BattlegroundsStats.FinalBoard(game with { FinalBoard = "not json" }));
        Assert.Empty(BattlegroundsStats.Combats(game with { CombatResults = null }));
    }

    [Fact]
    public void Hero_stats_group_skins_and_measure_pick_rate_and_combats()
    {
        static string Base(string id) => id.Replace("_SKIN_A", string.Empty, StringComparison.Ordinal);
        var games = new[]
        {
            Bg(1, 6000, 6060, "HERO_A", offered: "HERO_A,HERO_B,HERO_C,HERO_D",
                combats: """[{"turn":1,"result":"Win"},{"turn":2,"result":"Loss"}]"""),
            Bg(5, 6060, 6040, "HERO_A_SKIN_A", offered: "HERO_A_SKIN_A,HERO_B,HERO_E,HERO_F",
                combats: """[{"turn":1,"result":"Win"},{"turn":2,"result":"Tie"}]"""),
            Bg(3, 6040, null, "HERO_B", offered: "HERO_B,HERO_C,HERO_G,HERO_H"),
            Bg(2, null, null, "HERO_A"),
        };
        var global = new BgHeroGlobal(4.1, null, null, 0.2);

        var heroes = BattlegroundsStats.Heroes(games, Base, duos: false, id => id == "HERO_A" ? global : null);

        var a = heroes.Single(h => h.HeroCardId == "HERO_A");
        Assert.Equal(3, a.Games);
        Assert.Equal(40, a.NetRating);
        Assert.Equal(8 / 3.0, a.AveragePlacement, 6);
        Assert.Equal(new[] { 1, 1, 0, 0, 1, 0, 0, 0 }, a.PlacementCounts);
        Assert.Equal(1.0, a.CombatWinRate[1]);
        Assert.Equal(0.0, a.CombatWinRate[2]);
        Assert.Equal(2, a.Offered);
        Assert.Equal(1.0, a.PickRate);
        Assert.Same(global, a.Global);

        // HERO_B was offered three times and picked once.
        var b = heroes.Single(h => h.HeroCardId == "HERO_B");
        Assert.Equal(1 / 3.0, b.PickRate!.Value, 6);
        Assert.Null(b.NetRating);
        Assert.Null(b.Global);

        Assert.Equal("HERO_A", BattlegroundsStats.Sort(heroes, BgHeroSort.Games)[0].HeroCardId);
        Assert.Equal("HERO_A", BattlegroundsStats.Sort(heroes, BgHeroSort.AveragePlacement)[0].HeroCardId);
        Assert.Single(BattlegroundsStats.Sort(heroes, BgHeroSort.Games, minGames: 2));
    }

    [Fact]
    public void Duos_heroes_are_not_compared_with_solo_community_numbers()
    {
        var heroes = BattlegroundsStats.Heroes([Bg(2, null, null, mode: "Duos")], id => id, duos: true, _ => new BgHeroGlobal(4, null, null, null));
        var hero = Assert.Single(heroes);
        Assert.Null(hero.Global);
        Assert.Equal(4, hero.PlacementCounts.Count);
    }

    [Fact]
    public void Hero_meta_keeps_the_placement_distribution_and_has_no_invented_combat_win_rate()
    {
        using var doc = JsonDocument.Parse("""
            [{"hero":"Patchwerk","id":"TB_BaconShop_HERO_72","avg_placement":4.16,"pick_rate_value":25.0,
              "placement_distribution":["15.00%","13.00%","17.00%","11.00%","12.00%","11.00%","11.00%","10.00%"]}]
            """);

        var hero = Hearthpulse.Core.Api.HearthpulseData.ParseHeroes(doc.RootElement)["TB_BaconShop_HERO_72"];
        var global = BattlegroundsStats.GlobalOf(hero)!;

        Assert.Equal(8, global.PlacementShares!.Count);
        Assert.Equal(0.15, global.PlacementShares[0], 6);
        // The API's hero list has no per-turn combat win rate: none is shown rather than zeros.
        Assert.Null(global.CombatWinRate);
        Assert.Equal(0.25, global.PickRate);
        Assert.Equal(4.16, global.AveragePlacement);
    }

    [Theory]
    [InlineData("Bronze 10", 1)]
    [InlineData("Platinum 5", 36)]
    [InlineData("Diamond 1", 50)]
    [InlineData("Legend", 51)]
    public void Rank_text_maps_to_star_level(string rank, int stars) =>
        Assert.Equal(stars, ConstructedStats.RankOf(Ranked("Win", rank: rank))!.Value.StarLevel);

    [Fact]
    public void Rank_prefers_saved_numbers_and_reads_legend_ranks()
    {
        Assert.Equal((51, (int?)1204), ConstructedStats.RankOf(Ranked("Win", rank: "Legend 1,204")));
        Assert.Equal((12, (int?)null), ConstructedStats.RankOf(Ranked("Win", rank: "Platinum 5", stars: 12)));
        Assert.Null(ConstructedStats.RankOf(Ranked("Win", rank: "Unranked")));
        Assert.Null(ConstructedStats.RankOf(Ranked("Win")));
    }

    [Fact]
    public void Constructed_summary_splits_play_and_coin()
    {
        var games = new[]
        {
            Ranked("Win", 6, first: true, rank: "Gold 3", at: Now.AddHours(-3)),
            Ranked("Loss", 10, first: false, rank: "Gold 2", at: Now.AddHours(-2)),
            Ranked("Win", 8, first: false, rank: "Gold 3", at: Now.AddHours(-1)),
            Ranked("Win", 9, first: null, mode: "Casual"),
        };

        var summary = ConstructedStats.Summary(games);

        Assert.Equal(new WinLoss(4, 3, 1), summary.Record);
        Assert.Equal(0.75, summary.Record.WinRate);
        Assert.Equal(new WinLoss(1, 1, 0), summary.OnPlay);
        Assert.Equal(new WinLoss(2, 1, 1), summary.OnCoin);
        Assert.Equal(8.25, summary.AverageTurns);
        Assert.Equal(TimeSpan.FromMinutes(10), summary.AverageDuration);
        Assert.Equal("Gold 3", summary.LatestRank);
        Assert.Equal("Gold 2", summary.BestRank);
        Assert.Equal(3, ConstructedStats.RankTimeline(games).Count);
    }

    [Fact]
    public void Length_buckets_cover_every_game_length()
    {
        var buckets = ConstructedStats.ByLength([Ranked("Win", 4), Ranked("Loss", 7), Ranked("Win", 7), Ranked("Loss", 15)]);

        Assert.Equal(5, buckets.Count);
        Assert.Equal(new WinLoss(1, 1, 0), buckets[0].Record);
        Assert.Equal(new WinLoss(2, 1, 1), buckets[1].Record);
        Assert.Equal(WinLoss.Empty, buckets[2].Record);
        Assert.Equal(new WinLoss(1, 0, 1), buckets[4].Record);
    }

    [Fact]
    public void Groups_by_class_and_opponent_and_deck()
    {
        var games = new[]
        {
            Ranked("Win", cls: "MAGE", opp: "PRIEST", deck: "Spell Mage"),
            Ranked("Loss", cls: "MAGE", opp: "WARLOCK", deck: "Spell Mage"),
            Ranked("Win", cls: "ROGUE", opp: "PRIEST", deck: "Cycle Rogue"),
        };

        var classes = ConstructedStats.ByPlayerClass(games);
        Assert.Equal("MAGE", classes[0].Key);
        Assert.Equal(new WinLoss(2, 1, 1), classes[0].Record);
        Assert.Null(classes[0].GlobalWinRate);

        var opponents = ConstructedStats.ByOpponentClass(games);
        Assert.Equal(new WinLoss(2, 2, 0), opponents.Single(o => o.Key == "PRIEST").Record);

        var decks = ConstructedStats.Decks(games);
        Assert.Equal(2, decks.Count);
        Assert.Equal(new WinLoss(2, 1, 1), decks.Single(d => d.Name == "Spell Mage").Record);
    }

    [Fact]
    public void Arena_runs_follow_the_deck_id_or_the_loss_limit()
    {
        GameRecord Arena(string result, int hour, string cls = "MAGE", long? deck = null) =>
            Ranked(result, cls: cls, at: Now.AddHours(hour), mode: "Arena", deckId: deck) with { GameType = "GT_ARENA" };

        var games = new[]
        {
            // Run 1 (no deck ids): 2 wins then 3 losses ends it.
            Arena("Win", -20), Arena("Loss", -19), Arena("Win", -18), Arena("Loss", -17), Arena("Loss", -16),
            // Run 2: same class, a new run because the first one ended; its later games name deck 7 (an update
            // started saving ids mid-run), which does not split it.
            Arena("Win", -15), Arena("Win", -14), Arena("Win", -10, deck: 7), Arena("Loss", -9, deck: 7),
            // Run 3: a different deck id is a new run even with the same class.
            Arena("Win", -8, deck: 8),
        };

        var runs = ArenaStats.Runs(games);

        Assert.Equal(3, runs.Count);
        Assert.Equal((2, 3, true), (runs[0].Wins, runs[0].Losses, runs[0].Finished));
        Assert.Equal((3, 1, false), (runs[1].Wins, runs[1].Losses, runs[1].Finished));
        Assert.Equal(7, runs[1].DeckId);
        Assert.Equal(8, runs[2].DeckId);
        Assert.Single(runs[2].Games);

        var summary = ArenaStats.Summary(runs);
        Assert.Equal(3, summary.Runs);
        Assert.Equal(2, summary.AverageWins);
        Assert.Equal(3, summary.BestWins);
        Assert.Equal(new WinLoss(10, 6, 4), summary.Record);

        var mage = Assert.Single(ArenaStats.ByClass(runs));
        Assert.Equal(3, mage.Runs);
        Assert.Equal(2, mage.AverageWins);
    }

    [Fact]
    public void Arena_games_without_a_deck_id_join_the_run_of_the_same_deck()
    {
        GameRecord Arena(string result, int hour, long? deck) =>
            Ranked(result, cls: "PALADIN", at: Now.AddHours(hour), mode: "Arena", deckId: deck) with { GameType = "GT_ARENA" };

        // 4-1 with deck 7, then an imported game without an id, then 7 again: one 7-3 run that ends at 3 losses.
        var runs = ArenaStats.Runs(
        [
            Arena("Win", -10, 7), Arena("Win", -9, 7), Arena("Loss", -8, 7), Arena("Win", -7, 7), Arena("Win", -6, 7),
            Arena("Win", -5, null), Arena("Loss", -4, null), Arena("Win", -3, 7), Arena("Win", -2, 7), Arena("Loss", -1, 7),
        ]);

        var run = Assert.Single(runs);
        Assert.Equal((7, 3, true), (run.Wins, run.Losses, run.Finished));
        Assert.Equal(7, run.DeckId);
    }
}
