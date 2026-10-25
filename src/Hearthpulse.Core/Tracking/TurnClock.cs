using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>One turn of a Constructed game: whose it was, when the log started it, its time limit and how long it took.</summary>
/// <param name="PlayerId">The PLAYER_ID of the player whose turn it was.</param>
/// <param name="Seconds">How long it took; null while it runs.</param>
public sealed record TurnTiming(int Turn, int PlayerId, DateTime StartedAt, int LimitSeconds, double? Seconds);

/// <summary>
/// The turn being played and how long the turns took, from Power.log: a turn runs from the game's STEP=MAIN_READY
/// (TURN and CURRENT_PLAYER have moved just before) to its STEP=MAIN_END; its limit is the current player's TIMEOUT tag
/// (45, 55 then 75 seconds in current clients, set for the next turn when one ends). The rope shows in the last <see cref="RopeSeconds"/> of the limit.
/// </summary>
/// <param name="LocalActive">The local player's turn is running.</param>
/// <param name="TurnStartedAt">Log time (local) the running turn started.</param>
/// <param name="Running">False between a turn's end and the next one's start.</param>
public sealed record TurnClockView(
    int Turn,
    bool LocalActive,
    DateTime TurnStartedAt,
    int LimitSeconds,
    TimeSpan? LocalAverage,
    TimeSpan? OpponentAverage,
    TimeSpan? LocalLast,
    TimeSpan? OpponentLast,
    bool Running = true)
{
    public const int RopeSeconds = 15;

    /// <summary>A turn far past its limit is a replayed old log, not a live turn.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromSeconds(60);

    public TimeSpan Elapsed(DateTime now) => now > TurnStartedAt ? now - TurnStartedAt : TimeSpan.Zero;

    public TimeSpan Remaining(DateTime now)
    {
        var left = TimeSpan.FromSeconds(LimitSeconds) - Elapsed(now);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>The turn is in its last <see cref="RopeSeconds"/>: the game burns the rope.</summary>
    public bool Rope(DateTime now) => Remaining(now) <= TimeSpan.FromSeconds(RopeSeconds);

    /// <summary>True when the running turn cannot be live (catching up on an old log): the timer hides then.</summary>
    public bool IsStale(DateTime now) => Elapsed(now) > TimeSpan.FromSeconds(LimitSeconds) + Stale;

    /// <summary>"0:42" (minutes and seconds).</summary>
    public static string Format(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";
}

/// <summary>Keeps the turns of the current Constructed game (registered as a Power.log listener by the session).</summary>
public sealed class TurnClock : IPowerListener
{
    /// <summary>The turn limit when the player's TIMEOUT tag is missing.</summary>
    public const int DefaultLimit = 75;

    private const string CurrentPlayer = "CURRENT_PLAYER";

    private const string Timeout = "TIMEOUT";

    private readonly List<TurnTiming> _turns = new();

    private readonly Func<DateTime?> _lineTime;

    /// <param name="lineTime">
    /// The time of the log line being handled (<see cref="PowerParser.LastTimestamp"/>); the game's LastActivity is
    /// only stamped after a line, so at a turn's first line it is still the time of the line before.
    /// </param>
    public TurnClock(Func<DateTime?> lineTime) => _lineTime = lineTime;

    public IReadOnlyList<TurnTiming> Turns => _turns;

    public void OnGameStarted(GameState game) => _turns.Clear();

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag != GameTag.Step || entity.Id != game.GameEntityId || game.IsBattlegrounds)
        {
            return;
        }

        var at = _lineTime() ?? game.LastActivity;
        if (value == "MAIN_END")
        {
            Close(at);
            return;
        }

        // TURN is already 1 during the mulligan; a turn's clock starts when its main phase is ready.
        var turn = game.Turn;
        if (value != "MAIN_READY" || turn < 1 || (_turns.Count > 0 && _turns[^1].Turn == turn))
        {
            return;
        }

        Close(at);
        var player = game.Players.Values.FirstOrDefault(p => game.Entities.GetValueOrDefault(p.EntityId)?.GetInt(CurrentPlayer) == 1);
        var limit = player is null ? 0 : game.Entities.GetValueOrDefault(player.EntityId)?.GetInt(Timeout) ?? 0;
        _turns.Add(new TurnTiming(turn, player?.PlayerId ?? 0, at, limit > 0 ? limit : DefaultLimit, null));
    }

    public void OnGameEnded(GameState game) => Close(_lineTime() ?? game.LastActivity);

    private void Close(DateTime at)
    {
        if (_turns.Count > 0 && _turns[^1] is { Seconds: null } last)
        {
            _turns[^1] = last with { Seconds = Math.Max(0, (at - last.StartedAt).TotalSeconds) };
        }
    }

    /// <summary>The running turn and the averages so far; null before the first turn or in Battlegrounds.</summary>
    public TurnClockView? View(GameState game)
    {
        if (_turns.Count == 0 || game.IsBattlegrounds || game.LocalPlayerId == 0)
        {
            return null;
        }

        var current = _turns[^1];
        var local = game.LocalPlayerId;
        var done = _turns.Where(t => t.Seconds is not null && t.PlayerId != 0).ToList();
        TimeSpan? Average(bool mine) => done.Where(t => (t.PlayerId == local) == mine).Select(t => t.Seconds!.Value).DefaultIfEmpty(-1).Average() is var s and >= 0
            ? TimeSpan.FromSeconds(s)
            : null;
        TimeSpan? Last(bool mine) => done.LastOrDefault(t => (t.PlayerId == local) == mine)?.Seconds is { } s ? TimeSpan.FromSeconds(s) : null;
        return new TurnClockView(
            GameState.PlayerTurnOf(current.Turn),
            current.PlayerId == local,
            current.StartedAt,
            current.LimitSeconds,
            Average(true),
            Average(false),
            Last(true),
            Last(false),
            current.Seconds is null);
    }
}
