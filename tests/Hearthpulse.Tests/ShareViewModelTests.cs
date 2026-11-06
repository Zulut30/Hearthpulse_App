using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Share;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>The sharing features' view models (TASK-2026-11-07-growth-app), shared by both UIs.</summary>
[Collection(nameof(GlobalLanguage))]
public sealed class ShareViewModelTests
{
    private static readonly DateTime Start = new(2026, 11, 6, 18, 0, 0);

    private static int _key;

    public ShareViewModelTests() => Loc.Instance.SetLanguage(Loc.English);

    private static GameRecord Game(int minutes, string result, string mode = "Ranked") => new()
    {
        Key = "v" + Interlocked.Increment(ref _key),
        StartedAt = Start.AddMinutes(minutes),
        EndedAt = Start.AddMinutes(minutes + 8),
        Mode = mode,
        GameType = mode == "Battlegrounds" ? "GT_BATTLEGROUNDS" : "GT_RANKED",
        Format = mode == "Ranked" ? LadderSession.Standard : string.Empty,
        Result = result,
        PlayerClass = "PALADIN",
        OpponentClass = "SHAMAN",
        PlayerName = "Streamer#1234",
    };

    [Fact]
    public void A_picture_has_no_name_until_the_player_shows_the_battletag()
    {
        var settings = new AppSettings();
        var share = new ShareViewModel(ShareKind.SessionRecap, new SessionRecapCard(string.Empty, [], "empty"), "t", "f", settings, "Streamer#1234");

        Assert.True(share.CanSign);
        Assert.False(share.ShowBattleTag);
        Assert.Equal(string.Empty, share.BattleTag);

        share.ShowBattleTag = true;
        Assert.True(settings.ShareShowBattleTag);
        Assert.Equal("Streamer", share.BattleTag);

        var unsigned = new ShareViewModel(ShareKind.SessionRecap, new SessionRecapCard(string.Empty, [], "empty"), "t", "f", settings, "  ");
        Assert.False(unsigned.CanSign);
        Assert.False(unsigned.ShowBattleTag);
        Assert.Equal(string.Empty, unsigned.BattleTag);
    }

    [Fact]
    public void The_share_window_says_what_happened_and_switches_the_deck_layout()
    {
        var share = new ShareViewModel(ShareKind.Deck, new object(), "t", "f", new AppSettings(), null);

        Assert.True(share.HasLayouts);
        Assert.True(share.List);
        share.Cover = true;
        Assert.False(share.List);

        share.Copied(false);
        Assert.True(share.StatusIsError);
        Assert.Equal(Loc.T("Growth.Share.CopyFailed"), share.Status);
        share.Saved(@"C:\pics\deck.png");
        Assert.False(share.StatusIsError);
        Assert.Equal(Loc.F("Growth.Share.Saved", @"C:\pics\deck.png"), share.Status);
        share.Saved(null);
        Assert.True(share.StatusIsError);
    }

    [Fact]
    public void The_recap_card_puts_the_rank_now_first_and_the_rating_change_as_the_value()
    {
        GameRecord[] games =
        [
            Game(0, "Win") with { RankStarLevel = 40, DeckName = "Aggro" },
            Game(10, "Win") with { RankStarLevel = 41, DeckName = "Aggro" },
            Game(20, "Win") with { RankStarLevel = 42, DeckName = "Aggro" },
            Game(30, "Loss", "Battlegrounds") with { Placement = 6, RatingBefore = 6000, RatingAfter = 5960, HeroCardId = "H" },
        ];
        var card = MainViewModel.RecapCard(SessionRecap.Build(games, TimeSpan.FromHours(2)), id => id == "H" ? "Hero" : id);

        Assert.Equal(2, card.Sections.Count);
        var ranked = card.Sections[0];
        Assert.Equal(Loc.F("Growth.Recap.Ranked", Loc.T("Widgets.Ladder.Standard")), ranked.Title);
        Assert.Equal("HERO_04", ranked.HeroCardId);
        var rank = ranked.Stats.Single(s => s.Label == Loc.T("Growth.Recap.Rank"));
        Assert.Equal(new LadderRank(42, null).Display, rank.Value);
        Assert.Equal(1, rank.Sign);
        Assert.Contains(new LadderRank(40, null).Display, rank.Detail, StringComparison.Ordinal);
        Assert.Contains(ranked.Stats, s => s.Label == Loc.T("Growth.Recap.Streak") && s.Value == "3");

        var bg = card.Sections[1];
        Assert.Equal("H", bg.HeroCardId);
        var rating = bg.Stats.Single(s => s.Label == Loc.T("Growth.Recap.Mmr"));
        Assert.Equal("−40", rating.Value);
        Assert.Equal(-1, rating.Sign);
        Assert.Equal([new SharePlace("6", false)], bg.Places);
    }

    [Fact]
    public void An_arena_recap_shows_the_gold_with_the_rest_under_it()
    {
        var recap = new SessionRecapData(Start, Start.AddHours(1), [], [], [new ArenaRecap("MAGE", 7, 3, true, new ArenaRewards(150, 40, 1, 0, 0))]);

        var arena = Assert.Single(MainViewModel.RecapCard(recap, id => id).Sections);

        Assert.Equal("HERO_08", arena.HeroCardId);
        Assert.Equal(Loc.F("Growth.Recap.Gold", 150), arena.Stats[1].Value);
        Assert.DoesNotContain("150", arena.Stats[1].Detail, StringComparison.Ordinal);
        Assert.Equal(Loc.T("Growth.Recap.RunFinished"), arena.Stats[0].Detail);
    }

    [Fact]
    public void Timestamps_start_at_the_lead_in_before_the_first_game_and_follow_a_typed_time()
    {
        var settings = new AppSettings { TimestampLeadInSeconds = 60 };
        GameRecord[] games = [Game(0, "Win"), Game(15, "Loss"), Game(-60 * 24, "Win")];
        var stamps = new TimestampsViewModel(games, settings, id => id, Start.AddHours(1));

        Assert.Equal("17:59:00", stamps.StartText);
        Assert.StartsWith("00:00 Intro\n01:00 Game 1", stamps.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(2, stamps.RangeGames().Count);

        stamps.StartText = "18:00";
        Assert.StartsWith("00:00 Game 1", stamps.Text, StringComparison.Ordinal);

        stamps.StartText = "25:00";
        Assert.Equal(Loc.T("Growth.Timestamps.BadTime"), stamps.Error);
        Assert.False(stamps.HasText);

        stamps.LeadInText = "10";
        Assert.Equal(10, settings.TimestampLeadInSeconds);
        Assert.Equal("17:59:50", stamps.StartText);
        Assert.Equal(string.Empty, stamps.Error);

        stamps.RangeYesterday = true;
        Assert.Single(stamps.RangeGames());
        stamps.RangeToday = true;
        Assert.Equal(2, stamps.RangeGames().Count);

        stamps.Copied(true);
        Assert.Equal(Loc.T("Growth.Timestamps.Copied"), stamps.Status);
    }

    [Theory]
    [InlineData("14:05:30", true)]
    [InlineData("9:5", true)]
    [InlineData("24:00", false)]
    [InlineData("14-05", false)]
    [InlineData("", false)]
    [InlineData("1:2:3:4", false)]
    public void A_typed_time_reads_as_hours_minutes_and_seconds(string text, bool ok)
    {
        Assert.Equal(ok, TimestampsViewModel.TryTime(text, out _));
    }

    [Fact]
    public void A_typed_start_is_anchored_to_the_day_of_the_first_game()
    {
        var late = new[] { Game(5 * 60 + 50, "Win") };
        var stamps = new TimestampsViewModel(late, new AppSettings(), id => id, Start.AddHours(8));

        // The game starts at 23:50 and it is 2:00 now: "23:40" is that evening, not today.
        stamps.StartText = "23:40:00";
        Assert.StartsWith("00:00 Intro\n10:00 Game 1", stamps.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_window_line_reads_rank_rating_and_nothing_yet()
    {
        var ranked = StreamSessionLine.Of(new StreamSessionView(StreamSessionKind.Ranked, Start, LadderSession.Wild, new WinLoss(3, 2, 1), new LadderRank(40, null), new LadderRank(42, null), null, null, [], 3));
        Assert.Equal(new LadderRank(42, null).Display, ranked.Value);
        Assert.Equal(1, ranked.ChangeValue);
        Assert.Equal(Loc.F("Widgets.Ladder.Record", 2, 1), ranked.Record);
        Assert.Contains(Loc.T("Widgets.Ladder.Wild"), ranked.Title, StringComparison.Ordinal);

        var bg = StreamSessionLine.Of(new StreamSessionView(StreamSessionKind.Battlegrounds, Start, "Duos", WinLoss.Empty, null, null, 5000, 4900, [3], 1));
        Assert.Equal("−100", bg.Change);
        Assert.Equal(-1, bg.ChangeValue);
        Assert.Equal(string.Empty, bg.Record);

        var none = StreamSessionLine.Of(StreamSessionView.None(Start));
        Assert.Equal(Loc.T("Stream.Session.Idle"), none.Value);
    }

    [Fact]
    public void The_session_window_follows_the_snapshot_and_idles_while_off()
    {
        var overlay = new OverlayViewModel();
        var settings = new AppSettings();
        var view = new StreamSessionView(StreamSessionKind.Battlegrounds, Start, "Battlegrounds", WinLoss.Empty, null, null, 6000, 6050, [2, 7], 2);

        overlay.Update(Core.Tracking.LiveSnapshot.Empty with { StreamSession = view }, Core.Cards.CardDb.Empty, settings, null, null);
        Assert.Equal(StreamWindowState.Shown, overlay.SessionWindowState);
        Assert.Equal(StreamWindowState.Shown, overlay.StreamState("session"));
        Assert.Equal([new PlaceChip(2, true), new PlaceChip(7, false)], overlay.StreamSessionPlaces);
        Assert.True(overlay.HasStreamSessionPlaces);
        Assert.False(overlay.StreamSessionEmpty);

        overlay.Update(Core.Tracking.LiveSnapshot.Empty, Core.Cards.CardDb.Empty, settings, null, null);
        Assert.Equal(StreamWindowState.Idle, overlay.SessionWindowState);
        Assert.Equal(nameof(OverlayViewModel.SessionWindowState), OverlayViewModel.StreamStateProperty("session"));
    }

    [Fact]
    public void Start_a_new_session_keeps_the_time_and_tells_the_host()
    {
        var settings = new AppSettings();
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-share-{Guid.NewGuid():N}.db")), settings, TestDataFolder.App);
        var told = 0;
        model.StreamSettingsChanged += () => told++;

        Assert.Equal(string.Empty, model.StreamSessionStartText);
        model.StreamSessionWindow = true;
        model.ResetStreamSession();

        Assert.True(settings.StreamSessionWindow);
        Assert.NotNull(settings.StreamSessionStart);
        Assert.NotEqual(string.Empty, model.StreamSessionStartText);
        Assert.Equal(2, told);
        Assert.Null(model.DeckShare("not a code", "Deck", "MAGE"));
        Assert.True(model.SessionRecapShare().Recap!.IsEmpty);
    }
}
