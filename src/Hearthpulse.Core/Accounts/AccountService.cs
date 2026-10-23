using System.Diagnostics;

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
public sealed class AccountService(AccountClient client, TokenStore? store = null, Func<Uri, bool>? openBrowser = null)
{
    // Access tokens live 15 minutes; renew a minute early so a call never goes out with a dying one.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    /// <summary>A sign-in lasts 30 days from the site's approval; refreshing does not extend it.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // From this close to the end the card asks to sign in again.
    private static readonly TimeSpan EndingSoonWindow = TimeSpan.FromDays(2);

    // RFC 8628 3.5: every slow_down adds five seconds to the polling interval.
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    // Failures that say nothing about the session itself: keep it and try again later.
    private static readonly HashSet<string> Transient =
        ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED", "NOT_AVAILABLE", "BAD_RESPONSE"];

    // While waiting for the player a short outage is ridden out until the code expires.
    private static readonly HashSet<string> PollThrough = ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED"];

    private readonly TokenStore _store = store ?? new TokenStore(Path.Combine(AppPaths.Root, "account.dat"));

    // One token request at a time: the site rotates refresh tokens and treats reuse of an old one as theft
    // (it revokes the whole token family).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpires;
    private string? _refreshToken;

    /// <summary>Raised (on any thread) when the player signs in or out, or a stored session turns out to be gone.</summary>
    public event Action? Changed;

    public AccountUser? User { get; private set; }

    public bool SignedIn => User is not null;

    /// <summary>Paid access of the signed-in account; <see cref="Entitlements.None"/> when signed out.</summary>
    public Entitlements Entitlements => User?.Entitlements ?? Entitlements.None;

    /// <summary>The player's account page on the site (as <c>/me</c> names it).</summary>
    public Uri ProfileUrl { get; private set; } = client.DefaultProfileUrl;

    /// <summary>When the sign-in ends for good (UTC); null for a session saved before this was recorded.</summary>
    public DateTime? SessionExpires { get; private set; }

    /// <summary>The sign-in ends within two days: time to sign in again.</summary>
    public bool SessionEndingSoon => SessionExpires is { } end && end - DateTime.UtcNow < EndingSoonWindow;

    /// <summary>Waits between polls; tests replace it to run the flow without real time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

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
        var saved = _store.Load();
        if (saved is null)
        {
            return;
        }

        _refreshToken = saved.Value.RefreshToken;
        User = saved.Value.User;
        SessionExpires = saved.Value.ExpiresAt;
        Changed?.Invoke();
        var error = await RefreshAsync(withProfile: true, cancellationToken).ConfigureAwait(false);
        if (error is not null && !Transient.Contains(error.Code))
        {
            Diag.Info($"account: saved session rejected ({error.Code})");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Forget();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Signs in: asks the site for a code, hands it to <paramref name="showCode"/>, opens the confirmation page in the
    /// browser and polls until the player confirms, declines or the code expires. Cancelling
    /// <paramref name="cancellationToken"/> abandons the attempt. Signing in again while signed in (the 30 days are
    /// running out) replaces the session and revokes the old one.
    /// </summary>
    public async Task<AccountError?> SignInAsync(Action<DeviceCode> showCode, CancellationToken cancellationToken = default)
    {
        try
        {
            var device = await client.BeginAsync(cancellationToken).ConfigureAwait(false);
            showCode(device);
            if (!OpenSignInPage(device))
            {
                // Not fatal: the card shows the address and the code to type in.
                Diag.Info("account: browser did not open for the sign-in page");
            }

            var tokens = await PollUntilConfirmedAsync(device, cancellationToken).ConfigureAwait(false);
            // The site starts the 30 days when it issues the tokens; a minute off keeps the shown date on the safe side.
            var expires = DateTime.UtcNow + SessionLifetime - TimeSpan.FromMinutes(1);
            var (user, profileUrl) = await client.GetMeAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            string? previous;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                previous = _refreshToken;
                _refreshToken = null;
                Accept(tokens, user, profileUrl, expires);
            }
            finally
            {
                _gate.Release();
            }

            if (previous is not null)
            {
                await TryRevokeAsync(previous, CancellationToken.None).ConfigureAwait(false);
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
            await TryRevokeAsync(refresh, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A valid access token for an authenticated call, renewed when needed; null when signed out or offline.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessExpires - RefreshMargin)
        {
            return _accessToken;
        }

        return _refreshToken is null || await RefreshAsync(withProfile: false, cancellationToken).ConfigureAwait(false) is not null ? null : _accessToken;
    }

    private async Task<AccountTokens> PollUntilConfirmedAsync(DeviceCode device, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(device.Interval);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            await Delay(interval, cancellationToken).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(device.ExpiresIn))
            {
                throw new AccountException("EXPIRED_TOKEN", "the code ran out while waiting");
            }

            try
            {
                return await client.PollAsync(device, cancellationToken).ConfigureAwait(false);
            }
            catch (AccountException ex) when (ex.Code == "AUTHORIZATION_PENDING")
            {
            }
            catch (AccountException ex) when (ex.Code == "SLOW_DOWN")
            {
                interval += SlowDownStep;
            }
            catch (AccountException ex) when (PollThrough.Contains(ex.Code))
            {
                Diag.Info($"account: poll failed ({ex.Code}), still waiting");
                interval += SlowDownStep;
            }
        }
    }

    private async Task<AccountError?> RefreshAsync(bool withProfile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_refreshToken is null || User is null)
            {
                return Describe("SIGNED_OUT");
            }

            // Another caller may have refreshed while this one waited.
            if (!withProfile && _accessToken is not null && DateTime.UtcNow < _accessExpires - RefreshMargin)
            {
                return null;
            }

            var tokens = await client.RefreshAsync(_refreshToken, cancellationToken).ConfigureAwait(false);
            // The old refresh token is dead from here on: store the new pair before anything else uses the access token.
            Accept(tokens, User, profileUrl: null);
            if (!withProfile)
            {
                return null;
            }

            try
            {
                var (user, profileUrl) = await client.GetMeAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
                if (user.Id != User.Id)
                {
                    throw new AccountException("WRONG_ACCOUNT", "refreshed session belongs to another account");
                }

                Accept(tokens, user, profileUrl);
            }
            catch (AccountException ex) when (Transient.Contains(ex.Code))
            {
                Diag.Info($"account: profile not refreshed ({ex.Code})");
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

    private async Task TryRevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            await client.RevokeAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountException ex)
        {
            Diag.Info($"account: could not revoke the session ({ex.Code})");
        }
    }

    /// <summary>Takes new tokens; a rotated refresh token replaces the stored one at once. Call under the gate.</summary>
    private void Accept(AccountTokens tokens, AccountUser user, Uri? profileUrl, DateTime? sessionExpires = null)
    {
        _accessToken = tokens.AccessToken;
        _accessExpires = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
        _refreshToken = tokens.RefreshToken ?? _refreshToken;
        User = user;
        ProfileUrl = profileUrl ?? ProfileUrl;
        SessionExpires = sessionExpires ?? SessionExpires;
        if (_refreshToken is not null)
        {
            _store.Save(_refreshToken, user, SessionExpires);
        }

        Changed?.Invoke();
    }

    /// <summary>Drops the session here. Call under the gate.</summary>
    private void Forget()
    {
        _accessToken = null;
        _refreshToken = null;
        SessionExpires = null;
        var had = User is not null;
        User = null;
        _store.Delete();
        if (had)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Turns an error code into a message for the account card.</summary>
    internal static AccountError Describe(string code) => new(code, code switch
    {
        "CANCELLED" => "Sign-in cancelled.",
        "ACCESS_DENIED" => "Sign-in was declined on hearthpulse.net.",
        "EXPIRED_TOKEN" => "The code expired before it was confirmed. Press the button for a new one.",
        "OFFLINE" => "Can't reach hearthpulse.net. Check your connection and try again.",
        "SERVER_ERROR" or "TEMPORARILY_UNAVAILABLE" => "hearthpulse.net is busy or has a problem right now. Try again in a few minutes.",
        "RATE_LIMITED" => "Too many attempts. Wait a few minutes and try again.",
        "NOT_AVAILABLE" or "INVALID_REQUEST" or "INVALID_CLIENT" or "UNAUTHORIZED_CLIENT" or "UNSUPPORTED_GRANT_TYPE"
            => "Signing in from the app isn't available on hearthpulse.net right now. Everything else works without an account.",
        "INVALID_GRANT" or "INVALID_ACCESS_TOKEN" => "The sign-in has ended. Sign in again.",
        "INSUFFICIENT_SCOPE" => "hearthpulse.net didn't grant the access the app needs. Sign in again.",
        "BAD_RESPONSE" => "hearthpulse.net sent an answer the app doesn't understand. Try again later.",
        "SIGNED_OUT" => "You are signed out.",
        _ => "Something went wrong while signing in. Try again.",
    });
}
