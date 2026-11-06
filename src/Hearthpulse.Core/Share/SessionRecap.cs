using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Share;

/// <summary>One ranked format of a session recap (proposal A1): record, rank at the first and the latest game, the deck
/// that won most and the longest run of wins.</summary>
/// <param name="Format">FT_STANDARD or FT_WILD.</param>
/// <param name="BestDeck">The deck name that won most in the session (ties: better win rate, then more games); null with no win.</param>
public sealed record RankedRecap(string Format, WinLoss Record, LadderRank? Start, LadderRank? End, string? BestDeck, string? BestDeckClass, WinLoss? BestDeckRecord, int LongestWinStreak)
{
    /// <summary>Ranks climbed (negative when lost) below Legend; null when either end is unknown or both are Legend.</summary>
    public int? Steps => Start is { } s && End is { } e && !(s.IsLegend && e.IsLegend) ? e.StarLevel - s.StarLevel : null;

    /// <summary>Legend places gained when both ends are Legend with a place.</summary>
    public int? LegendPlaces => Start is { IsLegend: true, LegendRank: { } s } && End is { IsLegend: true, LegendRank: { } e } ? s - e : null;
}

/// <summary>One Battlegrounds mode of a session recap.</summary>
/// <param name="Mode">"Battlegrounds" or "Duos".</param>
/// <param name="Placements">Final places in play order (teams in Duos).</param>
/// <param name="BestHero">Base hero card id with the best average place (ties: more games); null without placed games.</param>
public sealed record BattlegroundsRecap(string Mode, IReadOnlyList<int> Placements, int? MmrStart, int? MmrEnd, string? BestHero, double? BestHeroPlace, int BestHeroGames)
{
    public int Games => Placements.Count;

    public double? AveragePlace => Placements.Count == 0 ? null : Placements.Average();

    /// <summary>The top half of the lobby: places 1-4 solo, teams 1-2 in Duos.</summary>
    public int TopCut => Mode == "Duos" ? 2 : 4;

    /// <summary>Share of games in the top half; null without games.</summary>
    public double? TopRate => Placements.Count == 0 ? null : Placements.Count(p => p <= TopCut) / (double)Placements.Count;

    public int? MmrDelta => MmrStart is { } s && MmrEnd is { } e ? e - s : null;
}

/// <summary>The Arena run played in the session (its whole record, also games before the session).</summary>
public sealed record ArenaRecap(string PlayerClass, int Wins, int Losses, bool Finished, ArenaRewards? Rewards);

/// <summary>
/// A play session as a shareable recap: every mode played in it. The session is the one the latest game belongs to, by
/// the ladder session's rule (<see cref="LadderSession"/>): it starts when Hearthstone starts or after a break longer
/// than the player's setting with no game.
/// </summary>
public sealed record SessionRecapData(
    DateTime Since,
    DateTime Until,
    IReadOnlyList<RankedRecap> Ranked,
    IReadOnlyList<BattlegroundsRecap> Battlegrounds,
    IReadOnlyList<ArenaRecap> Arena)
{
    public static SessionRecapData Empty { get; } = new(default, default, [], [], []);

    public bool IsEmpty => Ranked.Count == 0 && Battlegrounds.Count == 0 && Arena.Count == 0;

    public int Games => Ranked.Sum(r => r.Record.Games) + Battlegrounds.Sum(b => b.Games) + Arena.Sum(a => a.Wins + a.Losses);

    public TimeSpan Length => Until - Since;
}

/// <summary>Builds <see cref="SessionRecapData"/> from the history; pure, the same in both UIs.</summary>
public static class SessionRecap
{
    /// <summary>
    /// The session of the latest game (over or not): its first game's start and its last game's end. Null without games.
    /// A game before <paramref name="launchedAt"/> (the current Hearthstone start) belongs to an earlier session when the
    /// latest game is after it.
    /// </summary>
    public static (DateTime Since, DateTime Until)? LastSession(IReadOnlyList<GameRecord> games, TimeSpan breakAfter, DateTime? launchedAt = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var ordered = games.OrderByDescending(g => g.StartedAt).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        var bound = launchedAt is { } l && ordered[0].StartedAt >= l ? l : (DateTime?)null;
        var since = ordered[0].StartedAt;
        var until = End(ordered[0]);
        for (var i = 1; i < ordered.Count; i++)
        {
            var game = ordered[i];
            if ((bound is { } b && game.StartedAt < b) || since - End(game) > breakAfter)
            {
                break;
            }

            since = game.StartedAt;
            until = End(game) > until ? End(game) : until;
        }

        return (since, until);
    }

    /// <summary>The recap of the latest session (<see cref="LastSession"/>), or <see cref="SessionRecapData.Empty"/>.</summary>
    /// <param name="baseHero">Maps a Battlegrounds hero skin to its hero; identity when null.</param>
    /// <param name="rewards">Arena rewards typed in on the run's row, by <see cref="ArenaRun.Key"/>.</param>
    public static SessionRecapData Build(
        IReadOnlyList<GameRecord> games,
        TimeSpan breakAfter,
        DateTime? launchedAt = null,
        Func<string, string>? baseHero = null,
        IReadOnlyDictionary<string, ArenaRewards>? rewards = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        return LastSession(games, breakAfter, launchedAt) is { } session
            ? Of(games, session.Since, session.Until, baseHero, rewards)
            : SessionRecapData.Empty;
    }

    /// <summary>The recap of the games that started in [<paramref name="since"/>, <paramref name="until"/>].</summary>
    public static SessionRecapData Of(
        IReadOnlyList<GameRecord> games,
        DateTime since,
        DateTime until,
        Func<string, string>? baseHero = null,
        IReadOnlyDictionary<string, ArenaRewards>? rewards = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        var inSession = games.Where(g => !g.IsAbandoned && g.StartedAt >= since && g.StartedAt <= until).OrderBy(g => g.StartedAt).ToList();
        var ranked = new[] { LadderSession.Standard, LadderSession.Wild }
            .Select(format => Ranked(inSession.Where(g => g.Mode == "Ranked" && g.Format == format).ToList(), format))
            .OfType<RankedRecap>()
            .ToList();
        var battlegrounds = new[] { "Battlegrounds", "Duos" }
            .Select(mode => Battlegrounds(inSession.Where(g => g.Mode == mode && g.Placement > 0).ToList(), mode, baseHero ?? (h => h)))
            .OfType<BattlegroundsRecap>()
            .ToList();
        return new SessionRecapData(since, until, ranked, battlegrounds, Arena(games, inSession, rewards));
    }

    private static RankedRecap? Ranked(IReadOnlyList<GameRecord> games, string format)
    {
        if (games.Count == 0)
        {
            return null;
        }

        var ranks = games.Select(ConstructedStats.RankOf).OfType<(int StarLevel, int? LegendRank)>().Select(r => new LadderRank(r.StarLevel, r.LegendRank)).ToList();
        var best = games
            .Where(g => !string.IsNullOrWhiteSpace(g.DeckName))
            .GroupBy(g => (Name: g.DeckName!.Trim(), g.PlayerClass))
            .Select(group => (group.Key, Record: WinLoss.Of(group)))
            .Where(d => d.Record.Wins > 0)
            .OrderByDescending(d => d.Record.Wins)
            .ThenByDescending(d => d.Record.WinRate ?? 0)
            .ThenByDescending(d => d.Record.Games)
            .ThenBy(d => d.Key.Name, StringComparer.Ordinal)
            .Select(d => ((string Name, string Class, WinLoss Record)?)(d.Key.Name, d.Key.PlayerClass, d.Record))
            .FirstOrDefault();
        return new RankedRecap(
            format,
            WinLoss.Of(games),
            ranks.Count > 0 ? ranks[0] : null,
            ranks.Count > 0 ? ranks[^1] : null,
            best is { } b ? DeckName.Display(b.Name) : null,
            best?.Class,
            best?.Record,
            LongestWinStreak(games));
    }

    /// <summary>The most wins in a row; a tie or an unknown result breaks the run.</summary>
    public static int LongestWinStreak(IEnumerable<GameRecord> games)
    {
        int best = 0, run = 0;
        foreach (var game in games)
        {
            run = game.Result == "Win" ? run + 1 : 0;
            best = Math.Max(best, run);
        }

        return best;
    }

    private static BattlegroundsRecap? Battlegrounds(IReadOnlyList<GameRecord> games, string mode, Func<string, string> baseHero)
    {
        if (games.Count == 0)
        {
            return null;
        }

        var hero = games
            .Where(g => !string.IsNullOrEmpty(g.HeroCardId))
            .GroupBy(g => baseHero(g.HeroCardId), StringComparer.Ordinal)
            .Select(group => (Hero: group.Key, Place: group.Average(g => g.Placement), Games: group.Count()))
            .OrderBy(h => h.Place)
            .ThenByDescending(h => h.Games)
            .ThenBy(h => h.Hero, StringComparer.Ordinal)
            .Select(h => ((string Hero, double Place, int Games)?)h)
            .FirstOrDefault();
        return new BattlegroundsRecap(
            mode,
            games.Select(g => g.Placement).ToList(),
            games.Select(g => g.RatingBefore).FirstOrDefault(r => r is not null),
            games.Select(g => g.RatingAfter).LastOrDefault(r => r is not null),
            hero?.Hero,
            hero?.Place,
            hero?.Games ?? 0);
    }

    private static List<ArenaRecap> Arena(IReadOnlyList<GameRecord> all, IReadOnlyList<GameRecord> inSession, IReadOnlyDictionary<string, ArenaRewards>? rewards)
    {
        var played = inSession.Where(g => g.Mode == "Arena").ToHashSet();
        if (played.Count == 0)
        {
            return [];
        }

        // A run is counted whole: its games before the session are part of its record.
        return ArenaStats.Runs(all.Where(g => g.Mode == "Arena"))
            .Where(run => run.Games.Any(played.Contains))
            .OrderBy(run => run.Started)
            .Select(run => new ArenaRecap(
                run.PlayerClass,
                run.Wins,
                run.Losses,
                run.Finished,
                rewards is not null && rewards.TryGetValue(run.Key, out var r) && !r.IsEmpty ? r : null))
            .ToList();
    }

    private static DateTime End(GameRecord game) => game.EndedAt > game.StartedAt ? game.EndedAt : game.StartedAt;
}
