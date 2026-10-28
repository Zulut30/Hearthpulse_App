using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>The ladder session: where a session starts, the ranks at its ends, the record and the streak.</summary>
public sealed class LadderSessionTests
{
    private static readonly DateTime Launch = new(2026, 10, 25, 18, 0, 0);

    private static readonly TimeSpan Break = TimeSpan.FromHours(2);

    private static int _key;

    private static GameRecord Game(int minutes, string result, int stars, string format = LadderSession.Standard, string mode = "Ranked", int? legend = null) => new()
    {
        Key = "g" + Interlocked.Increment(ref _key),
        StartedAt = Launch.AddMinutes(minutes),
        EndedAt = Launch.AddMinutes(minutes + 8),
        Mode = mode,
        Format = format,
        Result = result,
        RankStarLevel = stars,
        RankLegend = legend,
    };

    [Fact]
    public void A_session_counts_ranked_games_per_format_with_the_ranks_at_both_ends_and_the_streak()
    {
        GameRecord[] games =
        [
            Game(0, "Win", 42),
            Game(10, "Loss", 43),
            Game(20, "Win", 42, LadderSession.Wild),
            Game(30, "Win", 43),
            Game(40, "Win", 44),
            Game(50, "Win", 45),
            Game(60, "Loss", 0, mode: "Casual"),
        ];

        var sessions = LadderSession.Build(games, Launch.AddMinutes(75), Launch, Break);

        var standard = sessions.Standard!;
        Assert.Equal(Launch, standard.Since);
        Assert.Equal(new WinLoss(5, 4, 1), standard.Record);
        Assert.Equal(42, standard.Start!.StarLevel);
        Assert.Equal(45, standard.Now!.StarLevel);
        Assert.Equal(3, standard.Steps);
        Assert.Equal(3, standard.Streak);
        Assert.Equal(new WinLoss(1, 1, 0), sessions.Wild!.Record);
    }

    [Fact]
    public void A_long_break_or_a_new_client_session_starts_a_new_session()
    {
        GameRecord[] games = [Game(0, "Win", 30), Game(10, "Win", 31), Game(10 + 8 + 150, "Loss", 32), Game(190, "Loss", 31)];

        // The third game came after a break longer than two hours: only it and the next one count.
        var after = LadderSession.Build(games, Launch.AddMinutes(200), Launch, Break).Standard!;
        Assert.Equal(new WinLoss(2, 0, 2), after.Record);
        Assert.Equal(-2, after.Streak);
        Assert.Equal(-1, after.Steps);

        // Hearthstone started again after the second game: the earlier games are another session.
        var relaunched = LadderSession.Build(games, Launch.AddMinutes(200), Launch.AddMinutes(100), TimeSpan.FromHours(6)).Standard!;
        Assert.Equal(2, relaunched.Record.Games);

        // Long after the last game the session is over.
        Assert.False(LadderSession.Build(games, Launch.AddHours(8), Launch, Break).Any);
    }

    [Fact]
    public void The_rank_of_the_game_being_played_is_the_current_one_and_legend_counts_places()
    {
        GameRecord[] games = [Game(0, "Win", 51, legend: 1500), Game(10, "Win", 51, legend: 1320)];
        var live = new RankInfo("FT_STANDARD", 5, 51, 1204, 120);

        var view = LadderSession.Build(games, Launch.AddMinutes(25), Launch, Break, LadderSession.Standard, live).Standard!;

        Assert.Equal(1204, view.Now!.LegendRank);
        Assert.Equal(296, view.LegendPlaces);
        Assert.Null(view.Steps);
        Assert.Equal(2, view.Record.Games);

        // The first game of a session, still running: only its rank is known.
        var first = LadderSession.Build([], Launch.AddMinutes(5), Launch, Break, LadderSession.Wild, new RankInfo("FT_WILD", 3, 25, 0, 120)).Wild!;
        Assert.Equal(0, first.Record.Games);
        Assert.Equal(25, first.Start!.StarLevel);
    }

    [Fact]
    public void Lines_show_the_change_record_win_rate_and_a_streak_of_two_or_more()
    {
        var view = new LadderSessionView(LadderSession.Standard, Launch, new LadderRank(42, null), new LadderRank(45, null), new WinLoss(5, 4, 1), 3);

        var line = LadderSessionLine.From(view);

        Assert.Equal("Standard", line.Format);
        Assert.Equal("Diamond 9 → Diamond 6", line.Ranks);
        Assert.Equal("+3 ranks", line.Change);
        Assert.Equal(1, line.ChangeValue);
        Assert.Equal("4–1", line.Record);
        Assert.Equal("5 games", line.Games);
        Assert.Equal("3 wins in a row", line.Streak);
        Assert.Equal(string.Empty, LadderSessionLine.From(view with { Streak = 1 }).Streak);
    }
}
