using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>The sharing features' models (TASK-2026-11-07-growth-app): session recap, OBS session, timestamps, year, deck.</summary>
[Collection(nameof(GlobalLanguage))]
public sealed class ShareTests
{
    private static readonly DateTime Start = new(2026, 11, 6, 18, 0, 0);

    private static readonly TimeSpan Break = TimeSpan.FromHours(2);

    private static int _key;

    public ShareTests() => Loc.Instance.SetLanguage(Loc.English);

    private static GameRecord Game(int minutes, string result, string mode = "Ranked", int stars = 0, string format = LadderSession.Standard) => new()
    {
        Key = "s" + Interlocked.Increment(ref _key),
        StartedAt = Start.AddMinutes(minutes),
        EndedAt = Start.AddMinutes(minutes + 8),
        Mode = mode,
        GameType = mode switch { "Arena" => "GT_ARENA", "Battlegrounds" => "GT_BATTLEGROUNDS", _ => "GT_RANKED" },
        Format = mode == "Ranked" ? format : string.Empty,
        Result = result,
        RankStarLevel = stars > 0 ? stars : null,
        PlayerClass = "PALADIN",
        OpponentClass = "SHAMAN",
    };

    private static GameRecord Bg(int minutes, int place, int before, int after, string hero, string mode = "Battlegrounds") => Game(minutes, place <= 4 ? "Win" : "Loss", mode) with
    {
        Placement = place,
        RatingBefore = before,
        RatingAfter = after,
        HeroCardId = hero,
        PlayerClass = "NEUTRAL",
    };

    [Fact]
    public void The_recap_takes_the_latest_session_only_and_finds_the_best_deck_and_the_longest_streak()
    {
        GameRecord[] games =
        [
            Game(-600, "Win", stars: 30) with { DeckName = "Old" },
            Game(0, "Win", stars: 40) with { DeckName = "Aggro Paladin" },
            Game(10, "Win", stars: 41) with { DeckName = "Aggro Paladin" },
            Game(20, "Loss", stars: 42) with { DeckName = "Control Paladin" },
            Game(30, "Win", stars: 41) with { DeckName = "Aggro Paladin" },
            Game(40, "Win", stars: 42) with { DeckName = "Aggro Paladin" },
            Game(50, "Win", stars: 43) with { DeckName = "Aggro Paladin" },
        ];

        var recap = SessionRecap.Build(games, Break);

        Assert.Equal(Start, recap.Since);
        var ranked = Assert.Single(recap.Ranked);
        Assert.Equal(new WinLoss(6, 5, 1), ranked.Record);
        Assert.Equal(40, ranked.Start!.StarLevel);
        Assert.Equal(43, ranked.End!.StarLevel);
        Assert.Equal(3, ranked.Steps);
        Assert.Equal("Aggro Paladin", ranked.BestDeck);
        Assert.Equal(new WinLoss(5, 5, 0), ranked.BestDeckRecord);
        Assert.Equal(3, ranked.LongestWinStreak);
        Assert.Empty(recap.Battlegrounds);
        Assert.Equal(6, recap.Games);
    }

    [Fact]
    public void A_battlegrounds_recap_has_the_rating_ends_the_top_four_rate_and_the_best_hero_by_average_place()
    {
        GameRecord[] games =
        [
            Bg(0, 1, 6000, 6080, "HERO_A"),
            Bg(20, 5, 6080, 6050, "HERO_B"),
            Bg(40, 3, 6050, 6090, "HERO_A_SKIN"),
            Bg(60, 8, 6090, 6020, "HERO_C"),
            Game(70, "Abandoned", "Battlegrounds") with { Placement = 0 },
        ];

        var recap = SessionRecap.Build(games, Break, baseHero: h => h.Replace("_SKIN", string.Empty, StringComparison.Ordinal));

        var bg = Assert.Single(recap.Battlegrounds);
        Assert.Equal([1, 5, 3, 8], bg.Placements);
        Assert.Equal(4.25, bg.AveragePlace);
        Assert.Equal(0.5, bg.TopRate);
        Assert.Equal(6000, bg.MmrStart);
        Assert.Equal(6020, bg.MmrEnd);
        Assert.Equal(20, bg.MmrDelta);
        Assert.Equal("HERO_A", bg.BestHero);
        Assert.Equal(2, bg.BestHeroGames);
    }

    [Fact]
    public void A_duos_recap_counts_the_top_two_teams()
    {
        var recap = SessionRecap.Build([Bg(0, 2, 5000, 5040, "H", "Duos"), Bg(20, 3, 5040, 5010, "H", "Duos")], Break);

        var duos = Assert.Single(recap.Battlegrounds);
        Assert.Equal(2, duos.TopCut);
        Assert.Equal(0.5, duos.TopRate);
    }

    [Fact]
    public void An_arena_recap_counts_the_whole_run_and_its_entered_rewards()
    {
        GameRecord[] games =
        [
            Game(-30, "Win", "Arena") with { DeckId = 7 },
            Game(0, "Win", "Arena") with { DeckId = 7 },
            Game(10, "Loss", "Arena") with { DeckId = 7 },
        ];
        var key = ArenaStats.Runs(games).Single().Key;

        var recap = SessionRecap.Of(games, Start, Start.AddHours(1), rewards: new Dictionary<string, ArenaRewards> { [key] = new(150, 40, 1, 0, 0) });

        var run = Assert.Single(recap.Arena);
        Assert.Equal((2, 1), (run.Wins, run.Losses));
        Assert.Equal(150, run.Rewards!.Gold);
    }

    [Fact]
    public void No_game_gives_an_empty_recap()
    {
        Assert.True(SessionRecap.Build([], Break).IsEmpty);
        Assert.Null(SessionRecap.LastSession([], Break));
    }

    [Fact]
    public void A_game_before_the_hearthstone_start_belongs_to_an_earlier_session()
    {
        var since = SessionRecap.LastSession([Game(0, "Win"), Game(20, "Win")], Break, launchedAt: Start.AddMinutes(15))!.Value.Since;

        Assert.Equal(Start.AddMinutes(20), since);
    }

    [Fact]
    public void The_stream_session_counts_from_the_reset_when_it_is_later_than_the_ladder_session()
    {
        Assert.Equal(Start.AddMinutes(30), StreamSession.Since(Start.AddMinutes(30), Start, Start.AddHours(1)));
        Assert.Equal(Start, StreamSession.Since(Start.AddMinutes(-30), Start, Start.AddHours(1)));
        Assert.Equal(Start.AddHours(1), StreamSession.Since(null, null, Start.AddHours(1)));
    }

    [Fact]
    public void The_stream_session_shows_the_mode_played_last_with_its_progress()
    {
        GameRecord[] games =
        [
            Game(0, "Win", stars: 40),
            Game(10, "Loss", stars: 41),
            Bg(20, 2, 6000, 6060, "H"),
            Bg(40, 6, 6060, 6030, "H"),
        ];

        var bg = StreamSession.Build(games, Start);
        Assert.Equal(StreamSessionKind.Battlegrounds, bg.Kind);
        Assert.Equal([2, 6], bg.Placements);
        Assert.Equal(30, bg.MmrDelta);

        var ranked = StreamSession.Build(games, Start, liveFormat: LadderSession.Standard, liveRank: new RankInfo("FT_STANDARD", 0, 42, 0, 0));
        Assert.Equal(StreamSessionKind.Ranked, ranked.Kind);
        Assert.Equal(new WinLoss(2, 1, 1), ranked.Record);
        Assert.Equal(2, ranked.Steps);

        Assert.Equal(StreamSessionKind.None, StreamSession.Build(games, Start.AddHours(5)).Kind);
    }

    [Fact]
    public void Timestamps_start_with_an_intro_at_zero_and_number_the_games_from_the_recording_start()
    {
        GameRecord[] games =
        [
            Game(0, "Win"),
            Game(12, "Loss") with { OpponentClass = "MAGE" },
            Bg(30, 3, 6000, 6050, "HERO"),
            Game(-10, "Win"),
        ];

        var text = YouTubeTimestamps.Build(games, YouTubeTimestamps.DefaultStart([games[0]], 30), h => h == "HERO" ? "Rafaam" : h);

        Assert.Equal(
            "00:00 Intro\n00:30 Game 1 — Paladin vs Shaman (win)\n12:30 Game 2 — Paladin vs Mage (loss)\n30:30 Game 3 — Rafaam (place 3)",
            text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Timestamps_of_a_video_over_an_hour_have_hours_on_every_line()
    {
        Assert.Equal("0:00:05", YouTubeTimestamps.Offset(TimeSpan.FromSeconds(5), TimeSpan.FromHours(2)));
        Assert.Equal("1:02:03", YouTubeTimestamps.Offset(new TimeSpan(1, 2, 3), TimeSpan.FromHours(2)));
        Assert.Equal("12:30", YouTubeTimestamps.Offset(new TimeSpan(0, 12, 30), TimeSpan.FromMinutes(40)));
        Assert.Equal(string.Empty, YouTubeTimestamps.Build([Game(0, "Win")], Start.AddHours(1)));
    }

    [Fact]
    public void A_game_starting_at_the_recording_start_needs_no_intro_line()
    {
        var text = YouTubeTimestamps.Build([Game(0, "Tie")], Start);

        Assert.Equal("00:00 Game 1 — Paladin vs Shaman (tie)", text);
    }

    [Fact]
    public void The_year_in_review_counts_only_that_years_games()
    {
        GameRecord[] games =
        [
            Game(0, "Win", stars: 45) with { DeckName = "Aggro Paladin" },
            Game(10, "Win", stars: 46) with { DeckName = "Aggro Paladin" },
            Game(20, "Loss", stars: 47) with { DeckName = "Aggro Paladin" },
            Game(30, "Win", "Casual") with { DeckName = "Mage2", PlayerClass = "MAGE" },
            Bg(40, 1, 7000, 7100, "HERO_A"),
            Bg(60, 6, 7100, 7050, "HERO_A"),
            Bg(80, 4, 7050, 7080, "HERO_B"),
            Game(-60 * 24 * 365, "Win", stars: 51) with { RankLegend = 10 },
        ];

        var year = YearInReview.Build(games, 2026, new Dictionary<string, int> { ["CARD_A"] = 4, ["CARD_B"] = 9 });

        Assert.Equal(7, year.Games);
        Assert.Equal(new WinLoss(4, 3, 1), year.Record);
        Assert.Equal("PALADIN", year.FavouriteClass);
        Assert.Equal("Aggro Paladin", year.FavouriteDeck);
        Assert.Equal(3, year.FavouriteDeckGames);
        Assert.Equal(2, year.BestWinStreak);
        Assert.Equal(47, year.BestRank!.StarLevel);
        Assert.Equal(7100, year.BestRating);
        Assert.Equal(("CARD_B", 9), (year.TopCard, year.TopCardPlays));
        Assert.Equal(("HERO_A", 2), (year.FavouriteHero, year.FavouriteHeroGames));
        Assert.Equal(2 / 3.0, year.TopRate!.Value, 6);
        Assert.Equal(11, year.BestMonth);
        Assert.Equal(TimeSpan.FromMinutes(56), year.TimePlayed);
    }

    [Fact]
    public void An_empty_year_has_no_favourites()
    {
        var year = YearInReview.Build([], 2026);

        Assert.True(year.IsEmpty);
        Assert.Null(year.FavouriteClass);
        Assert.Null(year.BestRating);
        Assert.Null(year.TopRate);
    }

    [Theory]
    [InlineData(2026, 12, 1, false, 2026)]
    [InlineData(2027, 1, 15, false, 2026)]
    [InlineData(2026, 11, 30, false, null)]
    [InlineData(2026, 11, 6, true, 2026)]
    public void The_year_in_review_shows_from_december_to_january_or_with_the_dev_flag(int y, int m, int d, bool force, int? shown)
    {
        Assert.Equal(shown, YearInReview.ShownYear(new DateTime(y, m, d), force));
    }

    [Fact]
    public void The_dev_flag_takes_one_or_true()
    {
        Assert.True(YearInReview.Forced(_ => "1"));
        Assert.True(YearInReview.Forced(_ => "TRUE"));
        Assert.False(YearInReview.Forced(_ => null));
        Assert.False(YearInReview.Forced(_ => "0"));
    }

    [Fact]
    public void The_deck_picture_lists_the_code_by_cost_with_its_curve_and_dust()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            [
              {"id":"HERO_04","dbfId":671,"name":"Uther","type":"HERO","cardClass":"PALADIN","set":"CORE"},
              {"id":"A","dbfId":1,"name":"Alpha","cost":1,"rarity":"COMMON","type":"MINION","set":"EXPANSION"},
              {"id":"B","dbfId":2,"name":"Beta","cost":9,"rarity":"LEGENDARY","type":"MINION","set":"EXPANSION"},
              {"id":"C","dbfId":3,"name":"Gamma","cost":1,"rarity":"RARE","type":"SPELL","set":"CORE"}
            ]
            """)));
        var code = DeckString.Encode(new Dictionary<string, int> { ["A"] = 2, ["B"] = 1, ["C"] = 2 }, "PALADIN", "FT_STANDARD", cards)!;

        var deck = DeckImage.From(code, "Mage2", null, cards)!;

        Assert.Equal(["Alpha", "Gamma", "Beta"], deck.Cards.Select(c => c.Name));
        Assert.Equal(5, deck.CardCount);
        Assert.Equal(4, deck.Curve[1]);
        Assert.Equal(1, deck.Curve[7]);
        Assert.Equal((2 * 40) + 1600, deck.Dust);
        Assert.Equal("PALADIN", deck.PlayerClass);
        Assert.Equal("Mage 2", deck.Name);
        Assert.True(deck.Cards[2].Legendary);
        Assert.Null(DeckImage.From("not a code", null, null, cards));
        Assert.Null(DeckImage.From(null, null, null, cards));
    }
}
