using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Share;

/// <summary>What the OBS window "Session" shows (proposal B1): Ranked or Battlegrounds, whichever was played last.</summary>
public enum StreamSessionKind
{
    /// <summary>No ranked or Battlegrounds game since the start.</summary>
    None,
    Ranked,
    Battlegrounds,
}

/// <summary>
/// Progress since the stream started: the rank (Ranked) or rating (Battlegrounds) at the first game and now, the record
/// or the last places. Only the player's own data.
/// </summary>
/// <param name="Format">FT_STANDARD / FT_WILD for Ranked; "Battlegrounds" / "Duos" for Battlegrounds.</param>
/// <param name="Placements">Battlegrounds: the last places, oldest first (at most <see cref="StreamSession.MaxPlacements"/>).</param>
public sealed record StreamSessionView(
    StreamSessionKind Kind,
    DateTime Since,
    string Format,
    WinLoss Record,
    LadderRank? RankStart,
    LadderRank? RankNow,
    int? MmrStart,
    int? MmrNow,
    IReadOnlyList<int> Placements,
    int Games)
{
    public static StreamSessionView None(DateTime since) => new(StreamSessionKind.None, since, string.Empty, WinLoss.Empty, null, null, null, null, [], 0);

    public int? MmrDelta => MmrStart is { } s && MmrNow is { } n ? n - s : null;

    /// <summary>Ranks climbed below Legend (negative when lost).</summary>
    public int? Steps => RankStart is { } s && RankNow is { } n && !(s.IsLegend && n.IsLegend) ? n.StarLevel - s.StarLevel : null;

    /// <summary>Legend places gained when both ends are Legend with a place.</summary>
    public int? LegendPlaces => RankStart is { IsLegend: true, LegendRank: { } s } && RankNow is { IsLegend: true, LegendRank: { } n } ? s - n : null;
}

/// <summary>Builds <see cref="StreamSessionView"/>; pure, used by the OBS window of both UIs.</summary>
public static class StreamSession
{
    /// <summary>The most places the window lists.</summary>
    public const int MaxPlacements = 8;

    /// <summary>
    /// When the stream session starts: the player's "Start a new session" time when there is one and it is later than
    /// <paramref name="ladderStart"/>, else the ladder session's start, else now (nothing played yet).
    /// </summary>
    public static DateTime Since(DateTime? reset, DateTime? ladderStart, DateTime now) =>
        reset is { } r && (ladderStart is null || r > ladderStart) ? r : ladderStart ?? reset ?? now;

    /// <param name="games">Games of any mode, any order.</param>
    /// <param name="liveFormat">The format of the ranked game being played, with <paramref name="liveRank"/>; null outside one.</param>
    /// <param name="liveRating">The Battlegrounds rating the client shows now (memory), with <paramref name="liveMode"/>.</param>
    public static StreamSessionView Build(
        IReadOnlyList<GameRecord> games,
        DateTime since,
        string? liveFormat = null,
        RankInfo? liveRank = null,
        string? liveMode = null,
        int? liveRating = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var played = games.Where(g => !g.IsAbandoned && g.StartedAt >= since && (g.Mode == "Ranked" || g.IsBattlegrounds))
            .OrderBy(g => g.StartedAt)
            .ToList();
        var latest = played.Count > 0 ? played[^1] : null;

        // A game being played decides the kind before it is saved.
        if (liveMode is "Battlegrounds" or "Duos" || (liveFormat is null && latest is { IsBattlegrounds: true }))
        {
            var mode = liveMode is "Battlegrounds" or "Duos" ? liveMode : latest!.Mode;
            return Battlegrounds(played.Where(g => g.Mode == mode).ToList(), since, mode, liveMode == mode ? liveRating : null);
        }

        var format = liveFormat ?? latest?.Format;
        if (format is null)
        {
            return StreamSessionView.None(since);
        }

        return Ranked(played.Where(g => g.Mode == "Ranked" && g.Format == format).ToList(), since, format, liveFormat == format ? liveRank : null);
    }

    private static StreamSessionView Ranked(IReadOnlyList<GameRecord> games, DateTime since, string format, RankInfo? live)
    {
        var ranks = games.Select(ConstructedStats.RankOf).OfType<(int StarLevel, int? LegendRank)>().Select(r => new LadderRank(r.StarLevel, r.LegendRank)).ToList();
        var liveRank = live is { StarLevel: > 0 } ? new LadderRank(Math.Min(live.StarLevel, RankInfo.LegendStarLevel), live.LegendRank > 0 ? live.LegendRank : null) : null;
        var start = ranks.Count > 0 ? ranks[0] : liveRank;
        var now = liveRank ?? (ranks.Count > 0 ? ranks[^1] : null);
        return new StreamSessionView(StreamSessionKind.Ranked, since, format, WinLoss.Of(games), start, now, null, null, [], games.Count);
    }

    private static StreamSessionView Battlegrounds(IReadOnlyList<GameRecord> games, DateTime since, string mode, int? liveRating)
    {
        var placed = games.Where(g => g.Placement > 0).ToList();
        var start = games.Select(g => g.RatingBefore).FirstOrDefault(r => r is not null) ?? liveRating;
        var now = (liveRating is > 0 ? liveRating : null) ?? games.Select(g => g.RatingAfter).LastOrDefault(r => r is not null) ?? start;
        return new StreamSessionView(
            StreamSessionKind.Battlegrounds,
            since,
            mode,
            WinLoss.Of(games),
            null,
            null,
            start,
            now,
            placed.Skip(Math.Max(0, placed.Count - MaxPlacements)).Select(g => g.Placement).ToList(),
            games.Count);
    }
}
