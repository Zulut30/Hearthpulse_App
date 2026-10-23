using System.Globalization;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Sends finished games to the player's profile on hearthpulse.net («My statistics»), through
/// <c>POST tracker/events/batch</c> with the account's device-flow token (scope <c>tracker.write</c>). Mapping and
/// rules: docs/profile-upload.md.
/// </summary>
/// <remarks>
/// Only while the player is signed in and has turned <see cref="AppSettings.UploadGamesToProfile"/> on (an opt-in:
/// null until asked): a game that ends otherwise is not queued, and turning it off drops what waits (the backfill
/// button sends past games on request). Events wait in
/// <see cref="ProfileUploadQueue"/>, go out in batches within the site's limits, and are retried with a growing pause
/// after an outage. Events the site rejects are dropped and logged, never retried. A Battlegrounds game waits up to
/// <see cref="RatingWait"/> for its new rating. Runs on its own task, never on the log thread or the UI thread.
/// </remarks>
public sealed class ProfileUploader : IDisposable
{
    /// <summary>The site takes 1-50 events per call.</summary>
    public const int MaxBatchEvents = 50;

    /// <summary>Bytes per call we allow ourselves; the site's limit is 5 MiB per request, 512 KiB per event.</summary>
    public const int MaxBatchBytes = 1024 * 1024;

    /// <summary>How long a Battlegrounds game waits for the rating read after it (the session waits 120 s).</summary>
    public static readonly TimeSpan RatingWait = TimeSpan.FromSeconds(130);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    private readonly AccountClient _client;
    private readonly Func<CancellationToken, Task<string?>> _accessToken;
    private readonly Func<string?> _accountId;
    private readonly Func<bool> _enabled;
    private readonly HistoryStore _history;
    private readonly ProfileUploadQueue _queue;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _failures;
    private DateTime _retryAt = DateTime.MinValue;
    private string _status = string.Empty;

    /// <param name="client">The site API (its base URL is the one the account signs in to).</param>
    /// <param name="accessToken">A valid access token, or null when signed out or offline (<see cref="AccountService.GetAccessTokenAsync"/>).</param>
    /// <param name="accountId">The signed-in account's user id, or null/empty while there is none.</param>
    /// <param name="enabled">Whether the player wants games sent (the setting).</param>
    /// <param name="history">The game history the events are built from.</param>
    /// <param name="queue">Where events wait (by default <c>profile-upload.json</c> in the data folder).</param>
    public ProfileUploader(AccountClient client, Func<CancellationToken, Task<string?>> accessToken, Func<string?> accountId,
        Func<bool> enabled, HistoryStore history, ProfileUploadQueue? queue = null)
    {
        _client = client;
        _accessToken = accessToken;
        _accountId = accountId;
        _enabled = enabled;
        _history = history;
        _queue = queue ?? new ProfileUploadQueue(Path.Combine(AppPaths.Root, "profile-upload.json"));
    }

    /// <summary>The account service's view of the same thing, for the hosts.</summary>
    public static ProfileUploader For(AccountClient client, AccountService account, Func<bool> enabled, HistoryStore history) =>
        new(client, account.GetAccessTokenAsync, () => account.User?.Id, enabled, history);

    /// <summary>Hero skins to base heroes (<c>CardDb.BaseHeroId</c>), so the site's hero stats match the global ones.</summary>
    public Func<string, string>? BaseHero { get; set; }

    /// <summary>UTC clock; tests move it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Raised (on any thread) when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>One line for the Settings card: queued games, the last send, or why sending waits.</summary>
    public string Status => _status;

    public int PendingCount => _queue.PendingCount;

    /// <summary>Whether sending is possible right now (signed in to a named account and the setting on).</summary>
    public bool Active => _enabled() && _accountId() is { Length: > 0 };

    /// <summary>Starts the background sender.</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
        Kick();
    }

    /// <summary>Asks the sender to look at the queue now (signed in, setting turned on, a game queued).</summary>
    public void Kick()
    {
        _retryAt = DateTime.MinValue;
        _wake.Release();
    }

    /// <summary>
    /// A game ended (or its rating arrived: the session raises the same outcome again with <c>RatingAfter</c>). Reads the
    /// row from the history and queues its event; a Battlegrounds game still waiting for its rating is held back.
    /// </summary>
    public void OnGameFinished(GameOutcome outcome)
    {
        if (outcome.Key is not { } key || !Active)
        {
            return;
        }

        try
        {
            var now = UtcNow();
            if (_history.Game(key) is not { } game)
            {
                return;
            }

            if (!Adopt())
            {
                return;
            }

            if (game.Mode == "Arena")
            {
                var arena = _history.Recent(200, "Arena");
                _queue.Enqueue(ProfileEvents.ForArena(arena.Select(g => g.Key == key ? game : g), new HashSet<string> { key }), now);
            }
            else if (ProfileEvents.ForGame(game, BaseHero) is { } e)
            {
                var waitForRating = game.IsBattlegrounds && game.RatingBefore is > 0 && game.RatingAfter is null && outcome.RatingAfter is null;
                _queue.Enqueue([e], waitForRating ? now + RatingWait : now);
            }

            Kick();
        }
        catch (Exception ex)
        {
            Diag.Error("profile upload: queuing a game failed", ex);
        }
    }

    /// <summary>
    /// Queues every past game of the history the site has not settled yet (the Settings button). Returns how many events
    /// were queued; 0 when signed out or the setting is off.
    /// </summary>
    public async Task<int> BackfillAsync(CancellationToken cancellationToken = default)
    {
        if (!Active || !Adopt())
        {
            return 0;
        }

        var count = await Task.Run(() =>
        {
            var games = _history.Since(DateTime.MinValue);
            var events = new List<ProfileEvent>();
            var constructed = _history.WithCards(games.Where(g => g.Mode is "Ranked" or "Casual").ToList());
            foreach (var game in games.Where(g => g.IsBattlegrounds).Concat(constructed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ProfileEvents.ForGame(game, BaseHero) is { } e && !_queue.Knows(e.EventId))
                {
                    events.Add(e);
                }
            }

            events.AddRange(ProfileEvents.ForArena(games.Where(g => g.Mode == "Arena")).Where(e => !_queue.Knows(e.EventId)));
            return _queue.Enqueue(events.OrderBy(e => e.OccurredAt, StringComparer.Ordinal), UtcNow());
        }, cancellationToken).ConfigureAwait(false);

        Diag.Info($"profile upload: backfill queued {count.ToString(CultureInfo.InvariantCulture)} events");
        Kick();
        return count;
    }

    /// <summary>
    /// Sends what is due, batch after batch, until the queue is empty, nothing more is due, or a send fails. Returns how
    /// long to wait before the next pass. The background loop calls it; tests call it directly.
    /// </summary>
    internal async Task<TimeSpan> PassAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendDueAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task<TimeSpan> SendDueAsync(CancellationToken cancellationToken)
    {
        if (!_enabled())
        {
            // Turned off (or never turned on): what was waiting is not sent later either.
            if (_queue.PendingCount > 0)
            {
                _queue.ClearPending();
            }

            SetStatus(string.Empty);
            return Idle;
        }

        if (_accountId() is not { Length: > 0 } || !Adopt())
        {
            SetStatus(string.Empty);
            return Idle;
        }

        var now = UtcNow();
        if (now < _retryAt)
        {
            return _retryAt - now;
        }

        var sent = 0;
        while (true)
        {
            now = UtcNow();
            var batch = _queue.NextBatch(now, MaxBatchEvents, MaxBatchBytes);
            if (batch.Count == 0)
            {
                var pending = _queue.PendingCount;
                SetStatus(pending > 0 ? Waiting(pending) : sent > 0 ? "All games are on your profile." : _status);
                return _queue.NextDue() is { } due && due > now ? Min(due - now, Idle) : Idle;
            }

            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return Backoff("not signed in or offline");
            }

            TrackerBatchAnswer answer;
            try
            {
                answer = await _client.PostEventsAsync(token, Body(batch), cancellationToken).ConfigureAwait(false);
            }
            catch (AccountException ex)
            {
                return Backoff(ex.Code);
            }

            if (answer.Answered)
            {
                foreach (var rejected in answer.Rejected)
                {
                    var e = batch.FirstOrDefault(b => b.EventId.Equals(rejected.EventId, StringComparison.OrdinalIgnoreCase));
                    Diag.Info($"profile upload: {e?.Type ?? "event"} {rejected.EventId} rejected: {rejected.Code}{(rejected.Field is { } f ? " (" + f + ")" : string.Empty)}");
                }

                LogWarnings(answer, batch);

                // Whatever the answer names is settled. An event it does not name (it should not happen) is retried later.
                _queue.Settle(answer.Accepted.Concat(answer.Rejected.Select(r => r.EventId)));
                var named = new HashSet<string>(answer.Accepted.Concat(answer.Rejected.Select(r => r.EventId)), StringComparer.OrdinalIgnoreCase);
                if (!batch.Any(b => named.Contains(b.EventId)))
                {
                    return Backoff("answer named no event");
                }

                sent += answer.Accepted.Count;
                _failures = 0;
                continue;
            }

            if (answer.Status is 400 or 413 or 422)
            {
                // The batch itself is refused (bad envelope or too large): retrying it as is would fail forever. A single
                // event is dropped; a larger batch is sent again one event at a time to find the culprit.
                if (batch.Count == 1)
                {
                    Diag.Info($"profile upload: {batch[0].Type} {batch[0].EventId} refused: HTTP {answer.Status.ToString(CultureInfo.InvariantCulture)} {answer.ErrorCode}");
                    _queue.Settle([batch[0].EventId]);
                    continue;
                }

                if (await SendOneByOneAsync(batch, token, cancellationToken).ConfigureAwait(false) is { } wait)
                {
                    return wait;
                }

                continue;
            }

            return Backoff(answer.ErrorCode ?? "HTTP " + answer.Status.ToString(CultureInfo.InvariantCulture));
        }
    }

    // A refused batch, again one event per call. Returns a wait when a call fails for another reason.
    private async Task<TimeSpan?> SendOneByOneAsync(IReadOnlyList<ProfileEvent> batch, string token, CancellationToken cancellationToken)
    {
        foreach (var e in batch)
        {
            TrackerBatchAnswer answer;
            try
            {
                answer = await _client.PostEventsAsync(token, Body([e]), cancellationToken).ConfigureAwait(false);
            }
            catch (AccountException ex)
            {
                return Backoff(ex.Code);
            }

            if (answer.Answered)
            {
                foreach (var rejected in answer.Rejected)
                {
                    Diag.Info($"profile upload: {e.Type} {rejected.EventId} rejected: {rejected.Code}{(rejected.Field is { } f ? " (" + f + ")" : string.Empty)}");
                }

                LogWarnings(answer, [e]);

                _queue.Settle([e.EventId]);
            }
            else if (answer.Status is 400 or 413 or 422)
            {
                Diag.Info($"profile upload: {e.Type} {e.EventId} refused: HTTP {answer.Status.ToString(CultureInfo.InvariantCulture)} {answer.ErrorCode}");
                _queue.Settle([e.EventId]);
            }
            else
            {
                return Backoff(answer.ErrorCode ?? "HTTP " + answer.Status.ToString(CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    // Accepted, but the site could not use the statistics: a mapping bug to fix here, so it goes to the log.
    private static void LogWarnings(TrackerBatchAnswer answer, IReadOnlyList<ProfileEvent> batch)
    {
        foreach (var warning in answer.Warnings)
        {
            var e = batch.FirstOrDefault(b => b.EventId.Equals(warning.EventId, StringComparison.OrdinalIgnoreCase));
            Diag.Info($"profile upload: {e?.Type ?? "event"} {warning.EventId} stored without statistics: {warning.Code}{(warning.Field is { } f ? " (" + f + ")" : string.Empty)}");
        }
    }

    internal static string Body(IEnumerable<ProfileEvent> batch) =>
        new JsonObject { ["events"] = new JsonArray([.. batch.Select(e => (JsonNode)e.ToJson())]) }.ToJsonString();

    // The queue belongs to the signed-in account; false while /me has not named it yet.
    private bool Adopt()
    {
        if (_accountId() is not { Length: > 0 } id)
        {
            return false;
        }

        _queue.Adopt(id);
        return true;
    }

    private TimeSpan Backoff(string reason)
    {
        _failures++;
        var wait = Min(TimeSpan.FromTicks(FirstRetry.Ticks << Math.Min(_failures - 1, 10)), MaxRetry);
        _retryAt = UtcNow() + wait;
        Diag.Info($"profile upload: sending paused ({reason}), next try in {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
        SetStatus(Waiting(_queue.PendingCount) + " Sending will be retried.");
        return wait;
    }

    private static string Waiting(int pending) =>
        pending == 1 ? "1 game waiting to be sent." : $"{pending.ToString(CultureInfo.InvariantCulture)} games waiting to be sent.";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void SetStatus(string status)
    {
        if (status != _status)
        {
            _status = status;
            StatusChanged?.Invoke();
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await PassAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Diag.Error("profile upload: send pass failed", ex);
                wait = Backoff(ex.GetType().Name);
            }

            try
            {
                await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        // Not disposed: the loop may still be registering on its token while it winds down.
        _stop.Cancel();
    }
}
