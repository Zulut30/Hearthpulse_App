using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;

namespace Hearthpulse.Core.Stats;

/// <summary>A ranked step: star level 1-50 (Bronze 10 ... Diamond 1), 51 = Legend with its place when known.</summary>
public sealed record LadderRank(int StarLevel, int? LegendRank)
{
    public bool IsLegend => StarLevel >= 51;

    /// <summary>The rank in the interface language ("Diamond 4", "Легенда 1 204").</summary>
    public string Display => new RankInfo(string.Empty, 0, StarLevel, LegendRank ?? 0, 0).Display;
}

/// <summary>
/// The ranked games of one format in the current play session: the rank at its first game and the latest rank known,
/// the record and the current streak. <see cref="Steps"/> is the ranks climbed (negative when lost) below Legend,
/// <see cref="LegendPlaces"/> the Legend places gained when both ranks are Legend with a place.
/// </summary>
/// <param name="Format">FT_STANDARD or FT_WILD.</param>
/// <param name="Streak">Wins in a row (positive) or losses in a row (negative) up to the latest game; 0 after a tie.</param>
public sealed record LadderSessionView(string Format, DateTime Since, LadderRank? Start, LadderRank? Now, WinLoss Record, int Streak)
{
    public int? Steps => Start is { } s && Now is { } n && !(s.IsLegend && n.IsLegend) ? n.StarLevel - s.StarLevel : null;

    public int? LegendPlaces => Start is { IsLegend: true, LegendRank: { } s } && Now is { IsLegend: true, LegendRank: { } n } ? s - n : null;
}

/// <summary>Both ranked formats of the session; a format without a ranked game in it is null.</summary>
public sealed record LadderSessions(LadderSessionView? Standard, LadderSessionView? Wild)
{
    public static LadderSessions None { get; } = new(null, null);

    public bool Any => Standard is not null || Wild is not null;
}

/// <summary>
/// Splits the history into play sessions. A session starts when Hearthstone starts (its log session) or after a break
/// longer than the player's setting with no game of any mode; it ends with such a break. The rank of a game is the
/// one the client showed when it began, so after the last game the "now" rank is that game's until the next one
/// starts (the live rank of the game in progress is used while it runs).
/// </summary>
public static class LadderSession
{
    public static readonly TimeSpan DefaultBreak = TimeSpan.FromHours(2);

    public const string Standard = "FT_STANDARD";

    public const string Wild = "FT_WILD";

    /// <param name="games">Games of any mode since at least <paramref name="launchedAt"/>, in any order.</param>
    /// <param name="launchedAt">When the current Hearthstone log session began; null when unknown.</param>
    /// <param name="live">The local player's rank in the ranked game being played, with its format; null outside one.</param>
    public static LadderSessions Build(IReadOnlyList<GameRecord> games, DateTime now, DateTime? launchedAt, TimeSpan breakAfter, string? liveFormat = null, RankInfo? live = null)
    {
        var since = SessionStart(games, now, launchedAt, breakAfter);
        if (since is null && live is null)
        {
            return LadderSessions.None;
        }

        var start = since ?? now;
        return new LadderSessions(
            Format(games, Standard, start, liveFormat == Standard ? live : null),
            Format(games, Wild, start, liveFormat == Wild ? live : null));
    }

    /// <summary>When the session the latest game belongs to began; null when there is no game or the session is over.</summary>
    internal static DateTime? SessionStart(IReadOnlyList<GameRecord> games, DateTime now, DateTime? launchedAt, TimeSpan breakAfter)
    {
        var ordered = games.Where(g => launchedAt is null || g.StartedAt >= launchedAt).OrderByDescending(g => g.StartedAt).ToList();
        if (ordered.Count == 0 || now - End(ordered[0]) > breakAfter)
        {
            return null;
        }

        var start = ordered[0].StartedAt;
        for (var i = 1; i < ordered.Count && start - End(ordered[i]) <= breakAfter; i++)
        {
            start = ordered[i].StartedAt;
        }

        return start;
    }

    private static DateTime End(GameRecord game) => game.EndedAt > game.StartedAt ? game.EndedAt : game.StartedAt;

    private static LadderSessionView? Format(IReadOnlyList<GameRecord> games, string format, DateTime since, RankInfo? live)
    {
        var ranked = games.Where(g => g.Mode == "Ranked" && g.Format == format && !g.IsAbandoned && g.StartedAt >= since)
            .OrderBy(g => g.StartedAt)
            .ToList();
        var liveRank = live is { StarLevel: > 0 } ? new LadderRank(Math.Min(live.StarLevel, 51), live.LegendRank > 0 ? live.LegendRank : null) : null;
        if (ranked.Count == 0 && liveRank is null)
        {
            return null;
        }

        var ranks = ranked.Select(ConstructedStats.RankOf).Where(r => r is not null).Select(r => new LadderRank(r!.Value.StarLevel, r.Value.LegendRank)).ToList();
        var first = ranks.Count > 0 ? ranks[0] : liveRank;
        var latest = liveRank ?? (ranks.Count > 0 ? ranks[^1] : null);
        return new LadderSessionView(format, since, first, latest, WinLoss.Of(ranked), Streak(ranked));
    }

    private static int Streak(IReadOnlyList<GameRecord> ranked)
    {
        var streak = 0;
        for (var i = ranked.Count - 1; i >= 0; i--)
        {
            var step = ranked[i].Result switch { "Win" => 1, "Loss" => -1, _ => 0 };
            if (step == 0 || (streak != 0 && Math.Sign(streak) != step))
            {
                break;
            }

            streak += step;
        }

        return streak;
    }
}

/// <summary>One format of the ladder session as display texts, the same in the overlay and on the Overview page.</summary>
/// <param name="ChangeValue">Sign of the rank change: positive when the player climbed.</param>
/// <param name="StreakValue">Positive for a win streak, negative for a loss streak.</param>
public sealed record LadderSessionLine(
    string Format,
    string Ranks,
    string Change,
    int ChangeValue,
    string Games,
    string Record,
    string WinRate,
    string Streak,
    int StreakValue)
{
    public static IReadOnlyList<LadderSessionLine> Of(LadderSessions sessions) =>
        new[] { sessions.Standard, sessions.Wild }.OfType<LadderSessionView>().Select(From).ToList();

    public static LadderSessionLine From(LadderSessionView view)
    {
        var start = view.Start?.Display ?? "—";
        var now = view.Now?.Display ?? "—";
        var (change, value) = view switch
        {
            { LegendPlaces: > 0 and var up } => (Localization.Loc.P("Widgets.Ladder.PlacesUp", up), 1),
            { LegendPlaces: < 0 and var down } => (Localization.Loc.P("Widgets.Ladder.PlacesDown", -down), -1),
            { Steps: > 0 and var up } => (Localization.Loc.P("Widgets.Ladder.StepsUp", up), 1),
            { Steps: < 0 and var down } => (Localization.Loc.P("Widgets.Ladder.StepsDown", -down), -1),
            _ => (string.Empty, 0),
        };
        var streak = view.Streak switch
        {
            >= 2 => Localization.Loc.P("Widgets.Ladder.WinStreak", view.Streak),
            <= -2 => Localization.Loc.P("Widgets.Ladder.LossStreak", -view.Streak),
            _ => string.Empty,
        };
        return new LadderSessionLine(
            Localization.Loc.T(view.Format == LadderSession.Wild ? "Widgets.Ladder.Wild" : "Widgets.Ladder.Standard"),
            start == now ? now : Localization.Loc.F("Widgets.Ladder.Ranks", start, now),
            change,
            value,
            Localization.Loc.P("Widgets.Ladder.Games", view.Record.Games),
            Localization.Loc.F("Widgets.Ladder.Record", view.Record.Wins, view.Record.Losses),
            view.Record.WinRate is { } rate ? rate.ToString("P0", System.Globalization.CultureInfo.CurrentCulture) : "—",
            streak,
            Math.Abs(view.Streak) >= 2 ? Math.Sign(view.Streak) : 0);
    }
}
