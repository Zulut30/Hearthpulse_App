using System.Globalization;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Sends finished games to the player's profile on hearthpulse.net («My statistics»), through
/// <c>POST tracker/events/batch</c> with the account's device-flow token (scope <c>tracker.write</c>). Mapping and
/// rules: docs/profile-upload.md.
/// </summary>
/// <remarks>
/// Only while the player is signed in and <see cref="AppSettings.UploadGamesToProfile"/> is on (the default): a game
/// that ends otherwise is not queued, and turning it off drops what waits. Past games go only
/// when the player asks (<see cref="StartBackfill"/>), read from the history a page at a time. The site keeps the first
/// copy of an event id forever, so every event is checked with <see cref="ProfileEventSchema"/> first and a failing one
/// is logged instead of sent, and a Battlegrounds game waits for its new rating up to <see cref="RatingWait"/> after it
/// ended (counted from the history row, so the wait survives a restart). Events wait in <see cref="ProfileUploadQueue"/>,
/// go out in batches within the site's limits, and are retried with a growing pause after an outage. Events the site
/// rejects, or stores without statistics (<c>warnings</c>), are logged and counted in <see cref="Status"/>, never
/// retried. Runs on its own task, never on the log thread or the UI thread.
/// </remarks>
public sealed class ProfileUploader : IDisposable
{
    /// <summary>The site takes 1-50 events per call.</summary>
    public const int MaxBatchEvents = 50;

    /// <summary>Bytes per call we allow ourselves; the site's limit is 5 MiB per request, 32 KiB per game event.</summary>
    public const int MaxBatchBytes = 1024 * 1024;

    /// <summary>History rows a backfill reads at a time, and the queue length below which it reads the next page.</summary>
    public const int BackfillPage = 1_000;

    /// <summary>How long after its end a Battlegrounds game waits for the rating read after it (the session waits 120 s).</summary>
    public static readonly TimeSpan RatingWait = TimeSpan.FromSeconds(130);

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    private readonly AccountClient _client;
    private readonly Func<AccountSession, CancellationToken, Task<string?>> _accessToken;
    private readonly Func<AccountSession?> _accountSession;
    private readonly Func<bool> _enabled;
    private readonly HistoryStore _history;
    private readonly ProfileUploadQueue _queue;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Deliberately not disposed: the loop may still be using it while it winds down after Dispose cancels _stop.")]
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Deliberately not disposed: the loop may still be using it while it winds down after Dispose cancels _stop.")]
    private readonly SemaphoreSlim _pass = new(1, 1);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Deliberately not disposed: the loop may still be using it while it winds down after Dispose cancels _stop.")]
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _failures;
    private DateTime _retryAt = DateTime.MinValue;
    private StatusState _state = new(StatusKind.None, 0, false, 0, 0, 0);
    private int _withoutStatistics;
    private int _refused;
    private int _notSent;
    private long _sendGeneration;
    private readonly Lock _queueChange = new();

    /// <param name="client">The site API (its base URL is the one the account signs in to).</param>
    /// <param name="accessToken">A token for the requested sign-in only, or null when it is gone or offline.</param>
    /// <param name="accountSession">The current named sign-in, or null while there is none.</param>
    /// <param name="enabled">Whether the player wants games sent (the setting).</param>
    /// <param name="history">The game history the events are built from.</param>
    /// <param name="queue">Where events wait (by default <c>profile-upload.json</c> in the data folder).</param>
    public ProfileUploader(AccountClient client, Func<AccountSession, CancellationToken, Task<string?>> accessToken, Func<AccountSession?> accountSession,
        Func<bool> enabled, HistoryStore history, ProfileUploadQueue? queue = null)
    {
        _client = client;
        _accessToken = accessToken;
        _accountSession = accountSession;
        _enabled = enabled;
        _history = history;
        _queue = queue ?? new ProfileUploadQueue(Path.Combine(AppPaths.Root, "profile-upload.json"));
    }

    /// <summary>The account service's view of the same thing, for the hosts.</summary>
    public static ProfileUploader For(AccountClient client, AccountService account, Func<bool> enabled, HistoryStore history) =>
        new(client, account.GetAccessTokenForSessionAsync, () => account.Session, enabled, history);

    /// <summary>Hero skins to base heroes (<c>CardDb.BaseHeroId</c>), so the site's hero stats match the global ones.</summary>
    public Func<string, string>? BaseHero { get; set; }

    /// <summary>UTC clock; tests move it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary><see cref="BackfillPage"/>; tests make it small.</summary>
    internal int BackfillPageSize { get; init; } = BackfillPage;

    /// <summary>Raised (on any thread) when <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>
    /// One line for the Settings card: games waiting (and whether past games follow), the last send, why sending waits,
    /// and how many games since the start went without statistics or were refused. Built on each read in the interface
    /// language, so it follows a language change.
    /// </summary>
    public string Status => Describe(_state);

    public int PendingCount => _queue.PendingCount;

    /// <summary>Whether a backfill is still reading past games from the history.</summary>
    public bool Backfilling => _queue.BackfillAfter is not null;

    /// <summary>Whether sending is possible right now (signed in to a named account and the setting on).</summary>
    public bool Active => _enabled() && _accountSession() is { AccountId.Length: > 0 };

    /// <summary>Starts the background sender.</summary>
    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
        Kick();
    }

    /// <summary>Asks the sender to look at the queue now (signed in, setting turned on, a game queued).</summary>
    public void Kick()
    {
        lock (_queueChange)
        {
            if (!_enabled())
            {
                // Even an off/on toggle while a request or history read waits invalidates its unsent batch.
                Interlocked.Increment(ref _sendGeneration);
                _queue.ClearPending();
            }
        }
        _retryAt = DateTime.MinValue;
        _wake.Release();
    }

    /// <summary>
    /// A game ended (or its rating arrived: the session raises the same outcome again with <c>RatingAfter</c>). Reads the
    /// row from the history and queues its event; a Battlegrounds game still waiting for its rating is held back.
    /// </summary>
    public void OnGameFinished(GameOutcome outcome)
    {
        var generation = Interlocked.Read(ref _sendGeneration);
        if (outcome.Key is not { } key || !Active)
        {
            return;
        }

        try
        {
            if (_history.Game(key) is not { } game || Adopt() is not { } session)
            {
                return;
            }

            if (game.Mode == "Arena")
            {
                var arena = _history.Recent(200, "Arena");
                Queue(ProfileEvents.ForArena(arena.Select(g => g.Key == key ? game : g), new HashSet<string> { key }).Select(e => (e, (GameRecord?)null)), session, generation);
            }
            else if (ProfileEvents.ForGame(game, BaseHero) is { } e)
            {
                Queue([(e, game)], session, generation);
            }

            _queue.Flush();
            Kick();
        }
        catch (Exception ex)
        {
            Diag.Error("profile upload: queuing a game failed", ex);
        }
    }

    /// <summary>
    /// Starts sending every past game of the history the site has not settled yet (the Settings button). The history is
    /// read a page at a time while the queue drains; <see cref="Status"/> shows the progress. False when signed out or the
    /// setting is off.
    /// </summary>
    public bool StartBackfill()
    {
        lock (_queueChange)
        {
            if (!Active || Adopt() is null)
            {
                return false;
            }

            if (_queue.BackfillAfter is null)
            {
                _queue.BackfillAfter = 0;
                _queue.Flush();
                Diag.Info("profile upload: backfill started");
            }
        }

        Kick();
        return true;
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
            _queue.Flush();
            _pass.Release();
        }
    }

    // Checks each event against the site's schema, then queues it; a Battlegrounds game without its new rating waits
    // until RatingWait after its end.
    private void Queue(IEnumerable<(ProfileEvent Event, GameRecord? Game)> events, AccountSession session, long generation)
    {
        var now = UtcNow();
        var due = new List<ProfileEvent>();
        var delayed = new List<(ProfileEvent Event, DateTime Until)>();
        foreach (var (e, game) in events)
        {
            if (ProfileEventSchema.Validate(e, now) is { } field)
            {
                _notSent++;
                Diag.Info($"profile upload: {e.Type} {e.EventId} not sent, the site would not read its {field}");
                continue;
            }

            if (game is { IsBattlegrounds: true, RatingBefore: > 0, RatingAfter: null } && game.EndedAt.ToUniversalTime() + RatingWait is var until && until > now)
            {
                delayed.Add((e, until));
            }
            else
            {
                due.Add(e);
            }
        }

        // Mapping/history reads can outlive a sign-in or an off/on toggle. Commit only while the original pass
        // remains valid, under the same lock as opt-out, so a late enqueue cannot undo ClearPending.
        lock (_queueChange)
        {
            if (!CanSend(session, generation)) return;
            foreach (var (e, until) in delayed)
            {
                _queue.Enqueue([e], until, session.AccountId);
            }
            _queue.Enqueue(due, now, session.AccountId);
        }
    }

    // Tops the queue up from the history while a backfill runs: a page of rows at a time, until the queue holds a page's
    // worth of events or the history ends. Arena runs are built from all Arena games at once, on the first page.
    private void FillFromBackfill(AccountSession session, long generation)
    {
        while (CanSend(session, generation) && _queue.BackfillAfter is { } after && _queue.PendingCount < BackfillPageSize)
        {
            if (after == 0)
            {
                var arena = ProfileEvents.ForArena(_history.Recent(int.MaxValue, "Arena"));
                Queue(arena.Where(e => !_queue.Knows(e.EventId)).Select(e => (e, (GameRecord?)null)), session, generation);
            }

            var page = _history.PageForUpload(after, BackfillPageSize);
            Queue(page.Where(g => g.Mode != "Arena")
                .Select(g => (Event: ProfileEvents.ForGame(g, BaseHero), Game: g))
                .Where(x => x.Event is not null && !_queue.Knows(x.Event.EventId))
                .Select(x => (x.Event!, (GameRecord?)x.Game)), session, generation);
            lock (_queueChange)
            {
                if (!CanSend(session, generation)) return;
                _queue.BackfillAfter = page.Count < BackfillPageSize ? null : page[^1].Id;
                _queue.Flush();
            }
            if (_queue.BackfillAfter is null)
            {
                Diag.Info("profile upload: backfill has queued every past game");
            }
        }
    }

    private async Task<TimeSpan> SendDueAsync(CancellationToken cancellationToken)
    {
        if (!_enabled())
        {
            // Turned off: what was waiting is not sent later either.
            _queue.ClearPending();
            SetStatus(StatusKind.None);
            return Idle;
        }

        var generation = Interlocked.Read(ref _sendGeneration);
        if (Adopt() is not { } session)
        {
            SetStatus(StatusKind.None);
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
            if (!CanSend(session, generation)) return Idle;
            FillFromBackfill(session, generation);
            now = UtcNow();
            var batch = _queue.NextBatch(now, MaxBatchEvents, MaxBatchBytes, session.AccountId);
            if (batch.Count == 0)
            {
                var pending = _queue.PendingCount;
                SetStatus(pending > 0 ? StatusKind.Waiting : sent > 0 || _state.Kind != StatusKind.None ? StatusKind.Done : StatusKind.None);
                return _queue.NextDue() is { } due && due > now ? Min(due - now, Idle) : Idle;
            }

            var token = await _accessToken(session, cancellationToken).ConfigureAwait(false);
            if (!CanSend(session, generation)) return Idle;
            if (token is null)
            {
                return Backoff("not signed in or offline");
            }

            TrackerBatchAnswer answer;
            try
            {
                answer = await _client.PostEventsAsync(token, Body(batch.Select(b => b.Event)), cancellationToken).ConfigureAwait(false);
            }
            catch (AccountException ex)
            {
                return Backoff(ex.Code);
            }

            if (!CanSend(session, generation)) return Idle;

            if (answer.Answered)
            {
                Record(answer, batch);

                // Whatever the answer names is settled. An event it does not name (it should not happen) is retried later.
                var named = new HashSet<string>(answer.Accepted.Concat(answer.Rejected.Select(r => r.EventId)), StringComparer.OrdinalIgnoreCase);
                _queue.Settle(batch.Where(b => named.Contains(b.Event.EventId)).Select(b => (b.Event.EventId, b.Version)), session.AccountId);
                if (!batch.Any(b => named.Contains(b.Event.EventId)))
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
                    Refuse(batch[0], answer, session.AccountId);
                    continue;
                }

                if (await SendOneByOneAsync(batch, session, generation, cancellationToken).ConfigureAwait(false) is { } wait)
                {
                    return wait;
                }

                continue;
            }

            return Backoff(answer.ErrorCode ?? "HTTP " + answer.Status.ToString(CultureInfo.InvariantCulture));
        }
    }

    // A refused batch, again one event per call. Returns a wait when a call fails for another reason.
    private async Task<TimeSpan?> SendOneByOneAsync(IReadOnlyList<QueuedEvent> batch, AccountSession session, long generation, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            if (!CanSend(session, generation)) return Idle;
            var token = await _accessToken(session, cancellationToken).ConfigureAwait(false);
            if (!CanSend(session, generation)) return Idle;
            if (token is null) return Backoff("not signed in or offline");
            TrackerBatchAnswer answer;
            try
            {
                answer = await _client.PostEventsAsync(token, Body([item.Event]), cancellationToken).ConfigureAwait(false);
            }
            catch (AccountException ex)
            {
                return Backoff(ex.Code);
            }

            if (!CanSend(session, generation)) return Idle;

            if (answer.Answered)
            {
                if (!answer.Accepted.Contains(item.Event.EventId, StringComparer.OrdinalIgnoreCase)
                    && !answer.Rejected.Any(r => r.EventId.Equals(item.Event.EventId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Backoff("answer named no event");
                }
                Record(answer, [item]);
                _queue.Settle([(item.Event.EventId, item.Version)], session.AccountId);
            }
            else if (answer.Status is 400 or 413 or 422)
            {
                Refuse(item, answer, session.AccountId);
            }
            else
            {
                return Backoff(answer.ErrorCode ?? "HTTP " + answer.Status.ToString(CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    private void Refuse(QueuedEvent item, TrackerBatchAnswer answer, string owner)
    {
        _refused++;
        Diag.Info($"profile upload: {item.Event.Type} {item.Event.EventId} refused: HTTP {answer.Status.ToString(CultureInfo.InvariantCulture)} {answer.ErrorCode}");
        _queue.Settle([(item.Event.EventId, item.Version)], owner);
    }

    // Logs and counts what the site rejected, and what it stored without statistics (warnings): either is final for the
    // id, so both are mapping bugs or limits to look at here.
    private void Record(TrackerBatchAnswer answer, IReadOnlyList<QueuedEvent> batch)
    {
        string TypeOf(string id) =>
            batch.FirstOrDefault(b => b.Event.EventId.Equals(id, StringComparison.OrdinalIgnoreCase))?.Event.Type ?? "event";

        foreach (var rejected in answer.Rejected)
        {
            _refused++;
            Diag.Info($"profile upload: {TypeOf(rejected.EventId)} {rejected.EventId} rejected: {rejected.Code}{Field(rejected.Field)}");
        }

        foreach (var warning in answer.Warnings)
        {
            _withoutStatistics++;
            Diag.Info($"profile upload: {TypeOf(warning.EventId)} {warning.EventId} stored without statistics: {warning.Code}{Field(warning.Field)}");
        }
    }

    private static string Field(string? field) => field is { } f ? " (" + f + ")" : string.Empty;

    internal static string Body(IEnumerable<ProfileEvent> batch) =>
        new JsonObject { ["events"] = new JsonArray([.. batch.Select(e => (JsonNode)e.ToJson())]) }.ToJsonString();

    // The queue belongs to the signed-in account; false while /me has not named it yet.
    private AccountSession? Adopt()
    {
        lock (_queueChange)
        {
            if (_accountSession() is not { AccountId.Length: > 0 } session)
            {
                return null;
            }

            _queue.Adopt(session.AccountId);
            return session;
        }
    }

    private bool CanSend(AccountSession session, long generation)
    {
        if (!_enabled())
        {
            _queue.ClearPending();
            return false;
        }

        return generation == Interlocked.Read(ref _sendGeneration)
            && _accountSession() == session && _queue.Owner == session.AccountId;
    }

    private TimeSpan Backoff(string reason)
    {
        _failures++;
        var wait = Min(TimeSpan.FromTicks(FirstRetry.Ticks << Math.Min(_failures - 1, 10)), MaxRetry);
        _retryAt = UtcNow() + wait;
        Diag.Info($"profile upload: sending paused ({reason}), next try in {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
        SetStatus(StatusKind.Retrying);
        return wait;
    }

    private static string Describe(StatusState s)
    {
        var text = s.Kind switch
        {
            StatusKind.Waiting or StatusKind.Retrying => string.Join(' ', new[]
            {
                Loc.P("Upload.Status.Waiting", s.Pending),
                s.Backfilling ? Loc.T("Upload.Status.MoreFollow") : null,
                s.Kind == StatusKind.Retrying ? Loc.T("Upload.Status.Retrying") : null,
            }.OfType<string>()),
            StatusKind.Done => Loc.T("Upload.Status.Done"),
            _ => string.Empty,
        };
        if (text.Length == 0)
        {
            return text;
        }

        var problems = new[]
        {
            s.WithoutStatistics > 0 ? Loc.P("Upload.Status.WithoutStatistics", s.WithoutStatistics) : null,
            s.Refused > 0 ? Loc.P("Upload.Status.Refused", s.Refused) : null,
            s.NotSent > 0 ? Loc.P("Upload.Status.NotSent", s.NotSent) : null,
        }.OfType<string>().ToList();
        return problems.Count == 0 ? text : text + " " + Loc.F("Upload.Status.SeeLog", string.Join(", ", problems));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void SetStatus(StatusKind kind)
    {
        var state = new StatusState(kind, _queue.PendingCount, Backfilling, _withoutStatistics, _refused, _notSent);
        if (state != _state)
        {
            _state = state;
            StatusChanged?.Invoke();
        }
    }

    private enum StatusKind
    {
        None,
        Waiting,
        Retrying,
        Done,
    }

    private sealed record StatusState(StatusKind Kind, int Pending, bool Backfilling, int WithoutStatistics, int Refused, int NotSent);

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
