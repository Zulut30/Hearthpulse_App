using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>Time frame of the My Stats pages.</summary>
public enum StatsPeriod
{
    All,

    /// <summary>The last 7 days.</summary>
    Week,

    /// <summary>The last 30 days.</summary>
    Month,

    /// <summary>
    /// The current ranked season: Hearthstone seasons run one calendar month. Battlegrounds seasons follow patches
    /// whose dates are not in any data the app has, so the Battlegrounds page calls this "This month".
    /// </summary>
    Season,
}

/// <summary>Wins and losses of a group of games (ties and unknown results count as games only).</summary>
public sealed record WinLoss(int Games, int Wins, int Losses)
{
    public static WinLoss Empty { get; } = new(0, 0, 0);

    /// <summary>Wins over decided games, or null without any.</summary>
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);

    public static WinLoss Of(IEnumerable<GameRecord> games)
    {
        int count = 0, wins = 0, losses = 0;
        foreach (var game in games)
        {
            count++;
            if (game.Result == "Win") wins++;
            else if (game.Result == "Loss") losses++;
        }

        return new WinLoss(count, wins, losses);
    }
}

public static class StatsFilter
{
    /// <summary>First moment inside the period, or null for all time.</summary>
    public static DateTime? Start(StatsPeriod period, DateTime now) => period switch
    {
        StatsPeriod.Week => now.AddDays(-7),
        StatsPeriod.Month => now.AddDays(-30),
        StatsPeriod.Season => new DateTime(now.Year, now.Month, 1, 0, 0, 0, now.Kind),
        _ => null,
    };

    /// <summary>The games of the period, oldest first. Abandoned games (no known outcome) are left out.</summary>
    public static IReadOnlyList<GameRecord> Apply(IEnumerable<GameRecord> games, StatsPeriod period, DateTime now)
    {
        var start = Start(period, now);
        return games.Where(g => !g.IsAbandoned && (start is null || g.StartedAt >= start)).OrderBy(g => g.StartedAt).ToList();
    }

    /// <summary>Ranked and Casual games of one format ("FT_STANDARD", "FT_WILD").</summary>
    public static bool IsConstructed(GameRecord game, string format) =>
        game.Mode is "Ranked" or "Casual" && string.Equals(game.Format, format, StringComparison.Ordinal);

    public static bool IsArena(GameRecord game) => game.Mode == "Arena";

    /// <summary>Total time of the games (each game's start to end; negative spans from bad clocks count as zero).</summary>
    public static TimeSpan TimePlayed(IEnumerable<GameRecord> games) =>
        TimeSpan.FromTicks(games.Sum(g => Math.Max(0, g.Duration.Ticks)));
}
