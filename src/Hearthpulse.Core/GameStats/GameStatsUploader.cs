using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.GameStats;

/// <summary>
/// Sends every finished game the player agreed to share as a game record (<see cref="GameStatsRecord"/>,
/// docs/game-stats.md). A game is built from its history row and its saved replay off the log and UI threads, waits
/// in <see cref="GameStatsQueue"/>, and goes out in batches of up to 20 with a growing pause after a failure. Turning
/// sharing off drops what waits and asks the pool to delete what this install sent; that request is kept until the
/// pool confirms it.
/// </summary>
public sealed class GameStatsUploader : IDisposable
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    /// <summary>Pause between two batches: the pool takes 6 requests a minute from one address.</summary>
    internal static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(12);

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly HistoryStore _history;
    private readonly Func<ReplayStore?> _replays;
    private readonly Func<CardDb?> _cards;
    private readonly string? _version;
    private readonly GameStatsQueue _queue;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Not disposed: the loop may still wait on it while it winds down after Dispose cancels _stop.")]
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Not disposed: the loop may still read its token while it winds down.")]
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _failures;

    public GameStatsUploader(AppSettings settings, HttpClient http, HistoryStore history, Func<ReplayStore?> replays, Func<CardDb?> cards, string? version, GameStatsQueue queue)
    {
        _settings = settings;
        _http = http;
        _history = history;
        _replays = replays;
        _cards = cards;
        _version = version;
        _queue = queue;
    }

    /// <summary>Records waiting to be sent.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Pause before a batch; tests make it zero.</summary>
    internal TimeSpan Delay { get; init; } = BatchDelay;

    /// <summary>Starts the background sender (live hosts only; a render never starts it).</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
        Kick();
    }

    public void Kick() => _wake.Release();

    /// <summary>
    /// A game ended (the session raises it again once a Battlegrounds rating arrives; only the first counts). Builds and
    /// queues its record when the player shares game records. Called on the log thread: the work runs on the pool.
    /// </summary>
    public void OnGameFinished(GameOutcome outcome)
    {
        if (outcome.RatingAfter is not null || outcome.Key is null || !GameStatsClient.Sharing(_settings))
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                if (Build(outcome) is { } record)
                {
                    _queue.Add(record);
                    Kick();
                }
            }
            catch (Exception ex)
            {
                Diag.Error("Game stats: building the game record failed", ex);
            }
        }).Forget("Game stats");
    }

    /// <summary>The record of a finished game from its history row and replay file.</summary>
    internal System.Text.Json.Nodes.JsonObject? Build(GameOutcome outcome)
    {
        if (outcome.Key is not { } key || _history.Game(key) is not { } game || game.FromOtherTracker)
        {
            return null;
        }

        var cards = _cards();
        var text = game.Replay is { Length: > 0 } file ? _replays()?.Load(file) : null;
        var pool = game.IsBattlegrounds && cards is not null
            ? PoolGame.From(outcome, cards.BaseHeroId, id => cards.Get(id)?.Races ?? [], details: true, PoolClient.Secret(_settings))
            : null;
        return GameStatsRecord.Build(game, GameStatsClient.RecordId(key, _settings), text, pool, cards, _version);
    }

    /// <summary>
    /// The player turned sharing off: what waits is dropped and the pool is asked to delete what this install sent.
    /// </summary>
    public void Withdraw()
    {
        _queue.Clear(forget: true);
        Kick();
    }

    /// <summary>One pass: a pending deletion first, then batches while there are records and the pool takes them.</summary>
    internal async Task<bool> PassAsync(CancellationToken cancellationToken)
    {
        var endpoint = PoolClient.Endpoint(_settings);
        if (!PoolClient.Available(_settings))
        {
            return true;
        }

        var install = GameStatsClient.InstallId(_settings);
        if (_queue.ForgetPending)
        {
            if (!await GameStatsClient.ForgetAsync(_http, endpoint, install, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            _queue.Forgotten();
        }

        while (GameStatsClient.Sharing(_settings) && _queue.Take(GameStatsClient.MaxBatch) is { Count: > 0 } batch)
        {
            switch (await GameStatsClient.SubmitAsync(_http, endpoint, install, batch, cancellationToken).ConfigureAwait(false))
            {
                case GameStatsAnswer.Later:
                    return false;
                case GameStatsAnswer.Refused:
                    Diag.Info($"Game stats: dropped {batch.Count} record(s) the pool refused");
                    break;
            }

            _queue.Remove(batch);
            if (_queue.Count > 0)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var wait = Idle;
                if (await PassAsync(cancellationToken).ConfigureAwait(false))
                {
                    _failures = 0;
                }
                else
                {
                    _failures++;
                    var ticks = FirstRetry.Ticks * (1L << Math.Min(_failures - 1, 6));
                    wait = TimeSpan.FromTicks(Math.Min(ticks, MaxRetry.Ticks));
                }

                await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Diag.Error("Game stats: sending failed", ex);
                await Task.Delay(FirstRetry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
