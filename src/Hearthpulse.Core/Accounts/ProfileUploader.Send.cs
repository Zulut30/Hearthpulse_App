using System.Globalization;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Accounts;

public sealed partial class ProfileUploader
{
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
                return Backoff("NO_TOKEN");
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
                    return Backoff("BAD_RESPONSE");
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

            return Backoff(FailureCode(answer));
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
            if (token is null) return Backoff("NO_TOKEN");
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
                    return Backoff("BAD_RESPONSE");
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
                return Backoff(FailureCode(answer));
            }
        }

        return null;
    }

    private void Refuse(QueuedEvent item, TrackerBatchAnswer answer, string owner)
    {
        _refused++;
        _activity?.CountGames(0, 1, 0);
        _activity?.Succeeded(SiteService.Games);
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

        // Kept across restarts for the account center (the counts above are since the start only).
        _activity?.CountGames(answer.Accepted.Count, answer.Rejected.Count, answer.Warnings.Count);
        _activity?.Succeeded(SiteService.Games);
    }

    private static string Field(string? field) => field is { } f ? " (" + f + ")" : string.Empty;

    /// <summary>
    /// Why a batch answer pauses sending, for the journal: a 5xx is the site failing for now (<c>SERVER_ERROR</c>) even
    /// when its body names a code; otherwise the site's code, else <c>HTTP_4xx</c>.
    /// </summary>
    private static string FailureCode(TrackerBatchAnswer answer)
    {
        if (answer.ErrorCode is { Length: > 0 } code)
        {
            Diag.Info($"profile upload: HTTP {answer.Status.ToString(CultureInfo.InvariantCulture)} {code}");
        }

        return answer.Status >= 500 || answer.ErrorCode is not { Length: > 0 }
            ? AccountService.StatusCode(answer.Status)
            : answer.ErrorCode;
    }
}
