using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

/// <summary>The signed-in HeartPulse account: the site's user id, its display name and its paid access.</summary>
public sealed record AccountUser(string Id, string DisplayName, Entitlements Entitlements);

/// <summary>Why signing in or refreshing failed, in words the player can act on.</summary>
public sealed record AccountError(string Code, string Message);

/// <summary>
/// The Hearthpulse account, which is the player's account on the HeartPulse site (hearthpulse.net). Signing in uses
/// the OAuth device authorization grant (RFC 8628, contract: docs/accounts-api.md): the app shows a short code, the
/// player confirms it on hearthpulse.net/connect in the browser, the app polls until the site says yes. The app never
/// sees a password. The session survives restarts: the refresh token is stored encrypted (<see cref="TokenStore"/>)
/// and replaced by the rotated one after every refresh. The access token lives in memory only.
/// </summary>
/// <remarks>
/// The site rotates refresh tokens with no grace window: presenting an already rotated one revokes the whole sign-in.
/// So once the site has been asked for tokens, the answer is stored whatever happens to the caller: token requests are
/// never cancelled by the caller (only the caller's wait is), and a new pair is on disk before anything else uses it.
/// What the client cannot cover is an answer lost on the wire after the site rotated; that needs a grace window on the
/// site.
/// </remarks>
public sealed class AccountService(AccountClient client, TokenStore? store = null, Func<Uri, bool>? openBrowser = null)
{
    /// <summary>A sign-in lasts 30 days from the token exchange; refreshing does not extend it.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // Access tokens live 15 minutes; renew a minute early so a call never goes out with a dying one.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    // From this close to the end the card asks to sign in again.
    private static readonly TimeSpan EndingSoonWindow = TimeSpan.FromDays(2);

    // How often the card's "signed in until" line is re-checked while the app runs (it lives in the tray for days).
    private static readonly TimeSpan PhaseCheck = TimeSpan.FromHours(1);

    // RFC 8628 3.5: every slow_down adds five seconds to the polling interval, for good.
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    // An outage while waiting backs off on top of the interval, at most this much, and resets once the site answers.
    private static readonly TimeSpan MaxOutageBackoff = TimeSpan.FromSeconds(30);

    // Shown until /me names the account; only seen after the app was closed between the token exchange and /me.
    private static readonly AccountUser PendingProfile = new(string.Empty, "HeartPulse player", Entitlements.None);

    // Failures that say nothing about the session itself: keep it and try again later.
    private static readonly HashSet<string> Transient =
        ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED", "NOT_AVAILABLE", "BAD_RESPONSE"];

    // While waiting for the player a short outage is ridden out until the code expires.
    private static readonly HashSet<string> PollThrough = ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED"];

    private readonly TokenStore _store = store ?? new TokenStore(Path.Combine(AppPaths.Root, "account.dat"));

    // One token request at a time (the site treats a second use of a refresh token as theft).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpires;
    private string? _refreshToken;
    private Timer? _phaseTimer;
    private int _phase;

    /// <summary>
    /// Raised (on any thread) when the player signs in or out, a stored session turns out to be gone, or the sign-in
    /// gets close to its end.
    /// </summary>
    public event Action? Changed;

    public AccountUser? User { get; private set; }

    public bool SignedIn => User is not null;

    /// <summary>Paid access of the signed-in account; <see cref="Entitlements.None"/> when signed out.</summary>
    public Entitlements Entitlements => User?.Entitlements ?? Entitlements.None;

    /// <summary>The player's account page on the site (as <c>/me</c> names it).</summary>
    public Uri ProfileUrl { get; private set; } = client.DefaultProfileUrl;

    /// <summary>When the sign-in ends for good (UTC); null when not signed in or not recorded.</summary>
    public DateTime? SessionExpires { get; private set; }

    /// <summary>The sign-in ends within two days, or has ended: time to sign in again.</summary>
    public bool SessionEndingSoon => SessionExpires is { } end && end - UtcNow() < EndingSoonWindow;

    /// <summary>
    /// The card's line about the sign-in's end: "Signed in until 22 Nov 2026", then a nudge to sign in again. Month
    /// names follow the interface language.
    /// </summary>
    public string SessionNote => SessionExpires is not { } end
        ? string.Empty
        : UtcNow() >= end
            ? Loc.F("Core.Account.SignInEnded", Day(end, "d MMM"))
            : SessionEndingSoon
                ? Loc.F("Core.Account.SignInEnds", Day(end, "d MMM"))
                : Loc.F("Core.Account.SignedInUntil", Day(end, "d MMM yyyy"));

    /// <summary>Waits between polls; tests replace it to run the flow without real time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>The clock for the sign-in's end and the code's expiry; tests replace it.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Opens the profile page in the browser.</summary>
    public bool OpenProfile() => (openBrowser ?? SystemBrowser.Open)(ProfileUrl);

    /// <summary>Opens the page where the player confirms <paramref name="device"/>, with the code filled in when the site allows.</summary>
    public bool OpenSignInPage(DeviceCode device) => (openBrowser ?? SystemBrowser.Open)(device.VerificationUriComplete ?? device.VerificationUri);

    /// <summary>
    /// Restores the session saved by a previous start. Without network the saved profile is shown as it was; a
    /// session the site rejects is forgotten. A good refresh also picks up a renamed profile and a changed subscription.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var saved = _store.Load(client.Site);
        if (saved is null)
        {
            return;
        }

        _refreshToken = saved.Value.RefreshToken;
        User = saved.Value.User;
        SessionExpires = saved.Value.ExpiresAt;
        WatchSessionEnd();
        Changed?.Invoke();
        await RefreshAsync(withProfile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in: asks the site for a code, opens the confirmation page in the browser, hands the code to
    /// <paramref name="showCode"/> (with whether the browser opened) and polls until the player confirms, declines or
    /// the code expires. Cancelling <paramref name="cancellationToken"/> stops the waiting; once the site has issued
    /// tokens the sign-in completes. Signing in again while signed in replaces the session and revokes the old one.
    /// </summary>
    public async Task<AccountError?> SignInAsync(Action<DeviceCode, bool> showCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var device = await client.BeginAsync(cancellationToken).ConfigureAwait(false);
            var opened = OpenSignInPage(device);
            if (!opened)
            {
                // Not fatal: the card shows the address and the code to type in.
                Diag.Info("account: browser did not open for the sign-in page");
            }

            showCode(device, opened);
            var tokens = await PollUntilConfirmedAsync(device, cancellationToken).ConfigureAwait(false);
            string? previous;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                previous = await AdoptAsync(tokens).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (previous is not null)
            {
                await TryRevokeAsync(previous).ConfigureAwait(false);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Describe("CANCELLED");
        }
        catch (AccountException ex)
        {
            Diag.Info($"account: sign-in failed ({ex.Code}: {ex.Message})");
            return Describe(ex.Code);
        }
    }

    /// <summary>Forgets the session here and revokes it on the site (best effort: offline it is still gone locally).</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? refresh;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            refresh = _refreshToken;
            Forget();
        }
        finally
        {
            _gate.Release();
        }

        if (refresh is not null)
        {
            await TryRevokeAsync(refresh).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A valid access token for an authenticated call, renewed when needed; null when signed out, offline, or when the
    /// site has ended the sign-in (the session is then forgotten). Cancelling stops the wait, not a refresh under way.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessExpires - RefreshMargin)
        {
            return _accessToken;
        }

        return _refreshToken is null || await RefreshAsync(withProfile: false, cancellationToken).ConfigureAwait(false) is not null ? null : _accessToken;
    }

    /// <summary>Re-checks how close the sign-in is to its end and raises <see cref="Changed"/> when that moved on.</summary>
    internal void CheckSessionPhase()
    {
        var phase = Phase();
        if (Interlocked.Exchange(ref _phase, phase) != phase)
        {
            Changed?.Invoke();
        }
    }

    private async Task<AccountTokens> PollUntilConfirmedAsync(DeviceCode device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(device.Interval);
        var outage = TimeSpan.Zero;
        var deadline = UtcNow().AddSeconds(device.ExpiresIn);
        while (true)
        {
            await Delay(interval + outage, cancellationToken).ConfigureAwait(false);
            if (UtcNow() > deadline)
            {
                throw new AccountException("EXPIRED_TOKEN", "the code ran out while waiting");
            }

            try
            {
                // Not cancelled: the poll that the site answers with tokens consumes the code, so its answer must
                // arrive even if the player pressed Cancel meanwhile.
                return await client.PollAsync(device, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AccountException ex) when (ex.Code == "AUTHORIZATION_PENDING")
            {
                outage = TimeSpan.Zero;
            }
            catch (AccountException ex) when (ex.Code == "SLOW_DOWN")
            {
                interval += SlowDownStep;
                outage = TimeSpan.Zero;
            }
            catch (AccountException ex) when (PollThrough.Contains(ex.Code))
            {
                Diag.Info($"account: poll failed ({ex.Code}), still waiting");
                outage = outage + SlowDownStep > MaxOutageBackoff ? MaxOutageBackoff : outage + SlowDownStep;
            }
        }
    }

    /// <summary>
    /// Makes the polled tokens the session. The pair is on disk before <c>/me</c> runs; without a profile the pair is
    /// handed back to the site and the previous session (if any) stays. Returns the replaced refresh token. Call under
    /// the gate.
    /// </summary>
    private async Task<string?> AdoptAsync(AccountTokens tokens)
    {
        // The site starts the 30 days when it issues the tokens; a minute off keeps the shown date on the safe side.
        var expires = UtcNow() + SessionLifetime - TimeSpan.FromMinutes(1);
        var (previous, previousUser, previousExpires) = (_refreshToken, User, SessionExpires);
        if (tokens.RefreshToken is { } fresh)
        {
            _store.Save(fresh, PendingProfile, expires, client.Site);
        }

        AccountUser user;
        Uri? profileUrl;
        try
        {
            (user, profileUrl) = await client.GetMeAsync(tokens.AccessToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AccountException)
        {
            if (tokens.RefreshToken is { } unused)
            {
                await TryRevokeAsync(unused).ConfigureAwait(false);
            }

            if (previous is not null && previousUser is not null)
            {
                _store.Save(previous, previousUser, previousExpires, client.Site);
            }
            else
            {
                _store.Delete();
            }

            throw;
        }

        _refreshToken = null;
        Accept(tokens, user, profileUrl, expires);
        return previous;
    }

    private async Task<AccountError?> RefreshAsync(bool withProfile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Started under the gate and never cancelled: once the site has rotated the token its answer must be stored,
        // even if this caller stops waiting. The task releases the gate itself.
        return await RefreshHeldAsync(withProfile).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountError?> RefreshHeldAsync(bool withProfile)
    {
        try
        {
            if (_refreshToken is null || User is null)
            {
                return Describe("SIGNED_OUT");
            }

            // Another caller may have renewed the token while this one waited for the gate.
            if (!withProfile && _accessToken is not null && DateTime.UtcNow < _accessExpires - RefreshMargin)
            {
                return null;
            }

            AccountTokens tokens;
            try
            {
                tokens = await client.RefreshAsync(_refreshToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AccountException ex) when (!Transient.Contains(ex.Code))
            {
                // Revoked, expired or rotated away: the site will not take this session back.
                Diag.Info($"account: saved session rejected ({ex.Code})");
                Forget();
                return Describe(ex.Code);
            }

            // The old refresh token is dead from here on: store the new pair before anything else uses the access token.
            Accept(tokens, User, profileUrl: null);
            if (!withProfile)
            {
                return null;
            }

            try
            {
                var (user, profileUrl) = await client.GetMeAsync(tokens.AccessToken, CancellationToken.None).ConfigureAwait(false);
                if (User.Id.Length > 0 && user.Id != User.Id)
                {
                    throw new AccountException("WRONG_ACCOUNT", "refreshed session belongs to another account");
                }

                Accept(tokens, user, profileUrl);
            }
            catch (AccountException ex) when (Transient.Contains(ex.Code))
            {
                Diag.Info($"account: profile not refreshed ({ex.Code})");
            }
            catch (AccountException ex)
            {
                // The new pair refreshes but is not good for the profile: give it back instead of leaving it alive.
                Diag.Info($"account: session dropped after /me ({ex.Code})");
                var refresh = _refreshToken;
                Forget();
                if (refresh is not null)
                {
                    await TryRevokeAsync(refresh).ConfigureAwait(false);
                }

                return Describe(ex.Code);
            }

            return null;
        }
        catch (AccountException ex)
        {
            return Describe(ex.Code);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TryRevokeAsync(string refreshToken)
    {
        try
        {
            await client.RevokeAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AccountException ex)
        {
            Diag.Info($"account: could not revoke the session ({ex.Code})");
        }
    }

    /// <summary>Takes new tokens; a rotated refresh token replaces the stored one at once. Call under the gate.</summary>
    private void Accept(AccountTokens tokens, AccountUser user, Uri? profileUrl, DateTime? sessionExpires = null)
    {
        // Disk first: GetAccessTokenAsync hands out a valid access token without taking the gate, so the new pair
        // must be stored before that token becomes visible.
        var refresh = tokens.RefreshToken ?? _refreshToken;
        var expires = sessionExpires ?? SessionExpires;
        if (refresh is not null && !_store.Save(refresh, user, expires, client.Site))
        {
            // The session works until the app closes; the next start will have to sign in again.
            Diag.Error("account: rotated refresh token kept in memory only");
        }

        _refreshToken = refresh;
        User = user;
        ProfileUrl = profileUrl ?? ProfileUrl;
        SessionExpires = expires;
        _accessExpires = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
        Volatile.Write(ref _accessToken, tokens.AccessToken);

        WatchSessionEnd();
        Changed?.Invoke();
    }

    /// <summary>Drops the session here. Call under the gate.</summary>
    private void Forget()
    {
        _accessToken = null;
        _refreshToken = null;
        SessionExpires = null;
        _phaseTimer?.Dispose();
        _phaseTimer = null;
        var had = User is not null;
        User = null;
        _store.Delete();
        if (had)
        {
            Changed?.Invoke();
        }
    }

    private void WatchSessionEnd()
    {
        _phase = Phase();
        _phaseTimer ??= new Timer(_ => CheckSessionPhase(), null, PhaseCheck, PhaseCheck);
    }

    // 0 = fine, 1 = ending within two days, 2 = ended.
    private int Phase() => SessionExpires is not { } end ? 0 : UtcNow() >= end ? 2 : SessionEndingSoon ? 1 : 0;

    private static string Day(DateTime utc, string format) =>
        utc.ToLocalTime().ToString(format, Loc.IsRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture);

    /// <summary>Turns an error code into a message for the account card.</summary>
    internal static AccountError Describe(string code) => new(code, Loc.T(code switch
    {
        "CANCELLED" => "Core.Account.Error.Cancelled",
        "ACCESS_DENIED" => "Core.Account.Error.AccessDenied",
        "EXPIRED_TOKEN" => "Core.Account.Error.ExpiredToken",
        "OFFLINE" => "Core.Account.Error.Offline",
        "SERVER_ERROR" or "TEMPORARILY_UNAVAILABLE" => "Core.Account.Error.ServerError",
        "RATE_LIMITED" => "Core.Account.Error.RateLimited",
        "NOT_AVAILABLE" or "INVALID_REQUEST" or "INVALID_CLIENT" or "UNAUTHORIZED_CLIENT" or "UNSUPPORTED_GRANT_TYPE"
            => "Core.Account.Error.NotAvailable",
        "INVALID_GRANT" or "INVALID_ACCESS_TOKEN" or "WRONG_ACCOUNT" => "Core.Account.Error.SignInEnded",
        "INSUFFICIENT_SCOPE" => "Core.Account.Error.InsufficientScope",
        "BAD_RESPONSE" => "Core.Account.Error.BadResponse",
        "SIGNED_OUT" => "Core.Account.Error.SignedOut",
        _ => "Core.Account.Error.Other",
    }));
}
