using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Twitch;

/// <summary>Where the link to the extension stands, for Settings → Stream.</summary>
public enum TwitchLinkState
{
    /// <summary>The switch is off: nothing is sent.</summary>
    Off,

    /// <summary>On, the site not asked yet.</summary>
    Checking,

    /// <summary>Not signed in to the Hearthpulse account (or the site ended the sign-in).</summary>
    SignedOut,

    /// <summary>
    /// The sign-in lacks the access the route needs (<c>403 insufficient_scope</c>): only signing in again helps, so
    /// nothing is tried until the sign-in changes.
    /// </summary>
    SignInAgain,

    /// <summary>The account has no Twitch linked (<c>404 twitch_not_linked</c>, or the status says so).</summary>
    NotLinked,

    /// <summary>The site's Twitch part is not set up (<c>503 twitch_not_configured</c>).</summary>
    NotConfigured,

    /// <summary>The site takes the deck: <see cref="TwitchLink.Login"/> is the channel.</summary>
    Connected,

    /// <summary>Anything else: offline, the site down or refusing; <see cref="TwitchLink.Code"/> says what.</summary>
    Error,
}

/// <summary>The link's state, the channel's login once known, the last error code, and the page that links Twitch.</summary>
public sealed record TwitchLink(TwitchLinkState State, string? Login, string? Code, Uri LinkUrl);

/// <summary>
/// Sends the deck to the Twitch extension through hearthpulse.net (docs/streaming.md, TASK-2026-11-07-twitch-extension):
/// <list type="bullet">
/// <item>only with <see cref="AppSettings.TwitchExtension"/> on and the account signed in;</item>
/// <item>the state of each live snapshot (<see cref="TwitchState.From"/>) waits <see cref="AppSettings.TwitchDelaySeconds"/>
/// in a queue, so the extension matches the stream's delay;</item>
/// <item>a state goes out only when it differs from the last one sent, at most once per <see cref="MinGap"/>; a deck
/// is sent again every <see cref="KeepAlive"/>; "none" goes once;</item>
/// <item>signed out, not linked, not configured, refused or offline: sending stops and is tried again after
/// <see cref="RetryPause"/> (or the site's longer <c>Retry-After</c>); <c>429</c> waits its <c>Retry-After</c>.</item>
/// </list>
/// Thread-safe: <see cref="Update"/> comes from the parser, the switches from the UI. <see cref="Changed"/> fires on any
/// thread after <see cref="Link"/> changes. Never throws to its callers.
/// </summary>
public sealed class TwitchPublisher : IDisposable
{
    /// <summary>The shortest time between two sends.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);

    /// <summary>A deck unchanged this long is sent again, so the site keeps it fresh for viewers who join.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>After a stop (signed out, not linked, not configured, offline, refused) the next try waits this long.</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromMinutes(1);

    /// <summary>A <c>429</c> without <c>Retry-After</c> waits this long.</summary>
    internal static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(5);

    /// <summary>The most states the delay queue holds; the oldest go first.</summary>
    internal const int MaxQueued = 4096;

    private readonly Lock _gate = new();
    private readonly TwitchClient _client;
    private readonly Func<CancellationToken, Task<string?>> _token;
    private readonly AppSettings _settings;
    private readonly Func<CardDb?> _cards;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly LinkedList<(DateTimeOffset Due, TwitchState State)> _queue = new();
    private readonly CancellationTokenSource _stop = new();

    private string? _queuedKey;
    private TwitchState? _wanted;
    private string? _sentKey;
    private string _sentMode = "none";
    private DateTimeOffset _sentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _triedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private bool _busy;
    private bool _wasOn;
    private bool _statusDue = true;
    private long _seq;
    private TwitchLink _link;

    /// <param name="client">The site's Twitch routes.</param>
    /// <param name="token">The account's access token, null when signed out (<see cref="AccountService.GetAccessTokenAsync"/>).</param>
    /// <param name="settings">The app's settings: the switch and the delay are read on every step.</param>
    /// <param name="cards">The card data, for the cards' dbf ids (0 until loaded).</param>
    /// <param name="time">The clock (tests pass a fake one).</param>
    public TwitchPublisher(TwitchClient client, Func<CancellationToken, Task<string?>> token, AppSettings settings, Func<CardDb?> cards, TimeProvider? time = null)
    {
        _client = client;
        _token = token;
        _settings = settings;
        _cards = cards;
        _time = time ?? TimeProvider.System;
        // Numbers keep growing across restarts of the app, so the extension never takes a new state for an old one.
        _seq = _time.GetUtcNow().ToUnixTimeMilliseconds();
        _link = new TwitchLink(settings.TwitchExtension ? TwitchLinkState.Checking : TwitchLinkState.Off, null, null, client.DefaultLinkUrl);
        _timer = _time.CreateTimer(_ => Pump(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The link as of the last contact with the site.</summary>
    public TwitchLink Link
    {
        get
        {
            lock (_gate)
            {
                return _link;
            }
        }
    }

    /// <summary><see cref="Link"/> changed (on any thread).</summary>
    public event Action? Changed;

    /// <summary>The last state sent (tests).</summary>
    internal string? SentKey
    {
        get
        {
            lock (_gate)
            {
                return _sentKey;
            }
        }
    }

    /// <summary>A send or a check is under way (tests wait for it to end).</summary>
    internal bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _busy;
            }
        }
    }

    /// <summary>Takes a live snapshot: its state joins the delay queue when it differs from the last one queued.</summary>
    public void Update(LiveSnapshot snapshot)
    {
        if (!_settings.TwitchExtension)
        {
            if (_wasOn)
            {
                Pump();
            }

            return;
        }

        TwitchState state;
        try
        {
            state = TwitchState.From(snapshot, _cards());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diag.Error("twitch: could not build the state", ex);
            return;
        }

        lock (_gate)
        {
            if (state.Key == _queuedKey)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var due = now + TimeSpan.FromSeconds(Math.Clamp(_settings.TwitchDelaySeconds, 0, AppSettings.MaxTwitchDelaySeconds));
            // A shorter delay than before: the newer state is due before older ones and replaces them.
            while (_queue.Last is { } last && last.Value.Due >= due)
            {
                _queue.RemoveLast();
            }

            _queue.AddLast((due, state));
            _queuedKey = state.Key;
            while (_queue.Count > MaxQueued)
            {
                _queue.RemoveFirst();
            }
        }

        Pump();
    }

    /// <summary>
    /// The switch, the delay or the sign-in changed: a stop's pause ends and the link is asked again, so the player sees
    /// the new state at once.
    /// </summary>
    public void Kick()
    {
        lock (_gate)
        {
            _pausedUntil = DateTimeOffset.MinValue;
            _triedAt = DateTimeOffset.MinValue;
            _statusDue = true;
        }

        Pump();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }

    /// <summary>One step: moves due states out of the queue and sends, asks the link, or sets the timer for later.</summary>
    internal void Pump()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        Func<Task>? work = null;
        var linkChanged = false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var on = _settings.TwitchExtension;
            if (!on)
            {
                linkChanged = SwitchedOff(ref work);
            }
            else
            {
                if (!_wasOn)
                {
                    _wasOn = true;
                    _statusDue = true;
                    _pausedUntil = DateTimeOffset.MinValue;
                    _triedAt = DateTimeOffset.MinValue;
                    linkChanged = SetLink(_link with { State = TwitchLinkState.Checking, Code = null });
                }

                while (_queue.First is { } first && first.Value.Due <= now)
                {
                    _wanted = first.Value.State;
                    _queue.RemoveFirst();
                }

                if (!_busy)
                {
                    work = Next(now);
                }
            }

            if (work is not null)
            {
                _busy = true;
            }
        }

        if (linkChanged)
        {
            Changed?.Invoke();
        }

        if (work is not null)
        {
            _ = RunAsync(work);
        }
    }

    // Off: the queue is dropped; a deck on the channel is cleared once with "none" (the delay does not apply).
    private bool SwitchedOff(ref Func<Task>? work)
    {
        _queue.Clear();
        _queuedKey = null;
        _wanted = null;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!_busy && _sentMode != "none" && _link.State != TwitchLinkState.SignedOut)
        {
            var body = TwitchState.None;
            work = () => SendAsync(body, clearing: true);
        }

        var changed = _wasOn && SetLink(_link with { State = TwitchLinkState.Off, Code = null });
        _wasOn = false;
        return changed;
    }

    private Func<Task>? Next(DateTimeOffset now)
    {
        if (now < _pausedUntil)
        {
            if (_pausedUntil == DateTimeOffset.MaxValue)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            else
            {
                Arm(_pausedUntil - now);
            }

            return null;
        }

        if (_statusDue)
        {
            return CheckStatusAsync;
        }

        var wanted = _wanted;
        var due = wanted is not null
            && (wanted.Key != _sentKey || (wanted.Deck is not null && now - _sentAt >= KeepAlive));
        if (due && now - _triedAt < MinGap)
        {
            Arm(_triedAt + MinGap - now);
            return null;
        }

        if (due)
        {
            return () => SendAsync(wanted!, clearing: false);
        }

        // Nothing now: wake for the next queued state or the keep-alive, whichever is first.
        var wake = _queue.First?.Value.Due ?? DateTimeOffset.MaxValue;
        if (wanted?.Deck is not null && _sentKey == wanted.Key)
        {
            var alive = _sentAt + KeepAlive;
            wake = alive < wake ? alive : wake;
        }

        if (wake == DateTimeOffset.MaxValue)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else
        {
            Arm(wake - now);
        }

        return null;
    }

    private void Arm(TimeSpan wait) => _timer.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_stop.IsCancellationRequested)
            {
                Diag.Error("twitch: a send failed", ex);
                Stop(TwitchLinkState.Error, "OTHER", null);
            }
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }

        Pump();
    }

    private async Task CheckStatusAsync()
    {
        lock (_gate)
        {
            _statusDue = false;
        }

        if (await _token(_stop.Token).ConfigureAwait(false) is not { } token)
        {
            Stop(TwitchLinkState.SignedOut, null, null);
            return;
        }

        var (answer, info) = await _client.GetStatusAsync(token, _stop.Token).ConfigureAwait(false);
        if (info is null)
        {
            Failed(answer);
            return;
        }

        var link = new TwitchLink(
            !info.Configured ? TwitchLinkState.NotConfigured : !info.Linked ? TwitchLinkState.NotLinked : TwitchLinkState.Connected,
            info.Linked ? info.Login : null,
            null,
            info.LinkUrl ?? _client.DefaultLinkUrl);
        if (link.State == TwitchLinkState.Connected)
        {
            Publish(link);
        }
        else
        {
            Stop(link.State, null, null, link.LinkUrl);
        }
    }

    private async Task SendAsync(TwitchState state, bool clearing)
    {
        long seq;
        lock (_gate)
        {
            _triedAt = _time.GetUtcNow();
            seq = ++_seq;
            if (clearing)
            {
                // Switched off: one try only, whatever comes of it.
                _sentMode = "none";
                _sentKey = null;
            }
        }

        var json = state.ToJson(seq);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > TwitchState.MaxBodyBytes)
        {
            Diag.Info("twitch: the deck is larger than the site takes, not sent");
            Sent(state);
            return;
        }

        if (await _token(_stop.Token).ConfigureAwait(false) is not { } token)
        {
            Stop(TwitchLinkState.SignedOut, null, null);
            return;
        }

        var answer = await _client.PostStateAsync(token, json, _stop.Token).ConfigureAwait(false);
        if (answer.Status is >= 200 and < 300)
        {
            Sent(state);
            if (!clearing)
            {
                var link = Link;
                if (link.Login is null)
                {
                    // Taken before the channel's name was known: ask the link once for the status line.
                    lock (_gate)
                    {
                        _statusDue = true;
                    }
                }

                Publish(link with { State = TwitchLinkState.Connected, Code = null });
            }

            return;
        }

        if (clearing)
        {
            return;
        }

        if (answer.Status == 429)
        {
            var wait = answer.RetryAfter ?? RateLimitPause;
            lock (_gate)
            {
                _pausedUntil = _time.GetUtcNow() + (wait < MinGap ? MinGap : wait);
            }

            return;
        }

        Failed(answer);
    }

    private void Sent(TwitchState state)
    {
        lock (_gate)
        {
            _sentKey = state.Key;
            _sentMode = state.Mode;
            _sentAt = _time.GetUtcNow();
        }
    }

    private void Failed(TwitchAnswer answer)
    {
        Diag.Info($"twitch: the site answered {answer.Status} {answer.Code}");
        var (state, code) = answer switch
        {
            { Status: 403, Code: "INSUFFICIENT_SCOPE" } => (TwitchLinkState.SignInAgain, (string?)null),
            { Status: 401 or 403 } => (TwitchLinkState.SignedOut, null),
            { Status: 404, Code: "TWITCH_NOT_LINKED" } => (TwitchLinkState.NotLinked, null),
            { Status: 503, Code: "TWITCH_NOT_CONFIGURED" } => (TwitchLinkState.NotConfigured, null),
            { Status: 0 } => (TwitchLinkState.Error, "OFFLINE"),
            { Status: 404 } => (TwitchLinkState.Error, "NOT_AVAILABLE"),
            { Status: 429 } => (TwitchLinkState.Error, "RATE_LIMITED"),
            { Status: >= 500 } => (TwitchLinkState.Error, answer.Code ?? "SERVER_ERROR"),
            _ => (TwitchLinkState.Error, answer.Code ?? "HTTP_" + answer.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        Stop(state, code, answer.RetryAfter);
    }

    /// <summary>Sending stops for <see cref="RetryPause"/> (or the site's longer wait); the next try asks the link first.</summary>
    private void Stop(TwitchLinkState state, string? code, TimeSpan? retryAfter, Uri? linkUrl = null)
    {
        bool changed;
        lock (_gate)
        {
            var pause = retryAfter is { } wait && wait > RetryPause ? wait : RetryPause;
            // The same token would be refused again: wait for a new sign-in (Kick) instead of a minute.
            _pausedUntil = state == TwitchLinkState.SignInAgain ? DateTimeOffset.MaxValue : _time.GetUtcNow() + pause;
            _statusDue = true;
            changed = _settings.TwitchExtension
                && SetLink(new TwitchLink(state, state == TwitchLinkState.Error ? _link.Login : null, code, linkUrl ?? _link.LinkUrl));
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Publish(TwitchLink link)
    {
        bool changed;
        lock (_gate)
        {
            changed = _settings.TwitchExtension && SetLink(link);
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool SetLink(TwitchLink link)
    {
        if (link == _link)
        {
            return false;
        }

        _link = link;
        return true;
    }
}
