using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>One Arena run: its games, oldest first.</summary>
/// <param name="Finished">True once the run reached its loss limit or 12 wins; the latest run may still be going.</param>
public sealed record ArenaRun(string PlayerClass, long? DeckId, DateTime Started, DateTime Ended, int Wins, int Losses, bool Finished, IReadOnlyList<GameRecord> Games);

/// <summary>Header numbers of the Arena page.</summary>
/// <param name="AverageWins">Mean wins of finished runs (null before the first finished run).</param>
public sealed record ArenaSummary(int Runs, WinLoss Record, double? AverageWins, int BestWins, TimeSpan TimePlayed);

/// <summary>Arena results of one class.</summary>
public sealed record ArenaClassStats(string PlayerClass, int Runs, double? AverageWins, WinLoss Record);

/// <summary>Pure aggregates over saved Arena games for the My Stats page.</summary>
public static class ArenaStats
{
    public const int MaxWins = 12;

    /// <summary>
    /// Losses that end a run: 3 in Arena. The log does not say which Arena variant a game belongs to beyond its game
    /// type, so a game type naming "UNDERGROUND" is given the 5 losses of Underground Arena (unverified on real logs).
    /// </summary>
    public static int MaxLosses(GameRecord game) => game.GameType.Contains("UNDERGROUND", StringComparison.Ordinal) ? 5 : 3;

    /// <summary>
    /// Splits Arena games into runs. A run is the games played with one deck id (Decks.log names the Arena deck); games
    /// without one are joined while the class stays the same and the run has not reached its loss or win limit.
    /// </summary>
    public static IReadOnlyList<ArenaRun> Runs(IEnumerable<GameRecord> games)
    {
        var runs = new List<List<GameRecord>>();
        List<GameRecord>? current = null;
        foreach (var game in games.Where(StatsFilter.IsArena).OrderBy(g => g.StartedAt))
        {
            if (current is null || !SameRun(current, game))
            {
                current = new List<GameRecord>();
                runs.Add(current);
            }

            current.Add(game);
        }

        return runs.Select(Build).ToList();
    }

    public static ArenaSummary Summary(IReadOnlyList<ArenaRun> runs)
    {
        var finished = runs.Where(r => r.Finished).ToList();
        var games = runs.SelectMany(r => r.Games).ToList();
        return new ArenaSummary(
            runs.Count,
            WinLoss.Of(games),
            finished.Count > 0 ? finished.Average(r => r.Wins) : null,
            runs.Count > 0 ? runs.Max(r => r.Wins) : 0,
            StatsFilter.TimePlayed(games));
    }

    /// <summary>Runs and results per class, most runs first.</summary>
    public static IReadOnlyList<ArenaClassStats> ByClass(IReadOnlyList<ArenaRun> runs) =>
        runs.Where(r => r.PlayerClass.Length > 0)
            .GroupBy(r => r.PlayerClass)
            .Select(g =>
            {
                var finished = g.Where(r => r.Finished).ToList();
                return new ArenaClassStats(g.Key, g.Count(), finished.Count > 0 ? finished.Average(r => r.Wins) : null, WinLoss.Of(g.SelectMany(r => r.Games)));
            })
            .OrderByDescending(c => c.Runs)
            .ThenByDescending(c => c.AverageWins ?? -1)
            .ToList();

    private static bool SameRun(List<GameRecord> run, GameRecord game)
    {
        var last = run[^1];
        // Once either game names its deck, only the same deck id is the same run.
        if (last.DeckId is > 0 || game.DeckId is > 0)
        {
            return last.DeckId == game.DeckId;
        }

        var wins = run.Count(g => g.Result == "Win");
        var losses = run.Count(g => g.Result == "Loss");
        return last.PlayerClass == game.PlayerClass && wins < MaxWins && losses < MaxLosses(last);
    }

    private static ArenaRun Build(List<GameRecord> games)
    {
        var wins = games.Count(g => g.Result == "Win");
        var losses = games.Count(g => g.Result == "Loss");
        return new ArenaRun(
            games[0].PlayerClass,
            games[0].DeckId is > 0 ? games[0].DeckId : null,
            games[0].StartedAt,
            games[^1].EndedAt,
            wins,
            losses,
            wins >= MaxWins || losses >= MaxLosses(games[^1]),
            games);
    }
}
