using System.Net.Sockets;

namespace Hearthpulse.Core.Accounts;

/// <summary>The signed-in HeartPulse account: the provider's <c>sub</c> and the profile's <c>name</c>.</summary>
public sealed record AccountUser(string Id, string DisplayName);

/// <summary>Why signing in or refreshing failed, in words the player can act on.</summary>
public sealed record AccountError(string Code, string Message);

/// <summary>
/// The Hearthpulse account, which is the player's account on the HeartPulse site (hearthpulse.net). Signing in
/// happens in the system browser through the site's OpenID Connect provider (<see cref="BrowserSignIn"/>, contract:
/// docs/accounts-api.md); the app never sees a password. The session survives restarts: the refresh token is stored
/// encrypted (<see cref="TokenStore"/>) and replaced by the rotated one after every refresh. The access token lives in
/// memory only.
/// </summary>
public sealed class AccountService(OidcClient oidc, TokenStore? store = null, Func<Uri, bool>? openBrowser = null)
{
    // Access tokens live five minutes; renew a little before that so a call never goes out with a dying one.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    // Failures that say nothing about the session itself: keep it and try again later.
    private static readonly HashSet<string> Transient =
        ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED", "NOT_AVAILABLE", "BAD_RESPONSE"];

    private readonly TokenStore _store = store ?? new TokenStore(Path.Combine(AppPaths.Root, "account.dat"));

    // One token request at a time: the provider rotates refresh tokens and treats reuse of an old one as theft.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpires;
    private string? _refreshToken;

    /// <summary>Raised (on any thread) when the player signs in or out, or a stored session turns out to be gone.</summary>
    public event Action? Changed;

    public AccountUser? User { get; private set; }

    public bool SignedIn => User is not null;

    /// <summary>How long a browser sign-in waits for the player.</summary>
    public TimeSpan SignInTimeout { get; init; } = BrowserSignIn.DefaultTimeout;

    /// <summary>The player's profile page on the site.</summary>
    public Uri ProfileUrl => oidc.ProfileUrl;

    /// <summary>Opens the profile page in the browser.</summary>
    public bool OpenProfile() => (openBrowser ?? SystemBrowser.Open)(ProfileUrl);

    /// <summary>
    /// Restores the session saved by a previous start. Without network the saved profile is shown as it was; a
    /// session the provider rejects is forgotten. A good refresh also picks up a renamed profile.
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
    /// Signs in through the browser and waits up to <see cref="SignInTimeout"/> for the player. Cancelling
    /// <paramref name="cancellationToken"/> abandons the attempt (the browser tab then just stops at the redirect).
    /// </summary>
    public async Task<AccountError?> SignInAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await new BrowserSignIn(oidc, openBrowser) { Timeout = SignInTimeout }.RunAsync(cancellationToken).ConfigureAwait(false);
            if (result.Tokens.RefreshToken is null)
            {
                Diag.Info("account: no refresh token issued; the session lasts until the app closes");
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _refreshToken = null;
                Accept(result.Tokens, result.User);
            }
            finally
            {
                _gate.Release();
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
        catch (SocketException ex)
        {
            // No loopback port to receive the redirect (unusual firewall or sandbox setups).
            Diag.Error("account: could not listen for the sign-in redirect", ex);
            return Describe("LISTENER_FAILED");
        }
    }

    /// <summary>Forgets the session here and revokes it on the provider (best effort: offline it is still gone locally).</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? refresh, access;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (refresh, access) = (_refreshToken, _accessToken);
            Forget();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            // Revoking the refresh token ends the whole grant, access tokens included.
            if (refresh is not null)
            {
                await oidc.RevokeAsync(refresh, "refresh_token", cancellationToken).ConfigureAwait(false);
            }
            else if (access is not null)
            {
                await oidc.RevokeAsync(access, "access_token", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AccountException ex)
        {
            Diag.Info($"account: could not revoke the session ({ex.Code})");
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

            var tokens = await oidc.RefreshAsync(_refreshToken, cancellationToken).ConfigureAwait(false);
            var user = User;
            if (withProfile)
            {
                try
                {
                    user = await oidc.ProfileAsync(tokens, nonce: null, cancellationToken).ConfigureAwait(false);
                }
                catch (AccountException ex) when (Transient.Contains(ex.Code))
                {
                    Diag.Info($"account: profile not refreshed ({ex.Code})");
                }
            }

            if (user.Id != User.Id)
            {
                throw new AccountException("BAD_TOKEN", "refreshed session belongs to another account");
            }

            Accept(tokens, user);
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

    /// <summary>Takes new tokens; a rotated refresh token replaces the stored one at once. Call under the gate.</summary>
    private void Accept(OidcTokens tokens, AccountUser user)
    {
        _accessToken = tokens.AccessToken;
        _accessExpires = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
        _refreshToken = tokens.RefreshToken ?? _refreshToken;
        User = user;
        if (_refreshToken is not null)
        {
            _store.Save(_refreshToken, user);
        }

        Changed?.Invoke();
    }

    /// <summary>Drops the session here. Call under the gate.</summary>
    private void Forget()
    {
        _accessToken = null;
        _refreshToken = null;
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
        "TIMEOUT" => "Sign-in timed out. Press the button to try again.",
        "ACCESS_DENIED" => "Sign-in was declined in the browser.",
        "BROWSER_FAILED" => "Couldn't open your browser. Check that a default browser is set and try again.",
        "OFFLINE" => "Can't reach hearthpulse.net. Check your connection and try again.",
        "SERVER_ERROR" or "TEMPORARILY_UNAVAILABLE" => "The HeartPulse site has a problem right now. Try again later.",
        "RATE_LIMITED" => "Too many attempts. Wait a minute and try again.",
        "NOT_AVAILABLE" or "INVALID_CLIENT" or "UNAUTHORIZED_CLIENT" or "INVALID_SCOPE" or "INVALID_REQUEST" or "UNSUPPORTED_RESPONSE_TYPE"
            => "Signing in from the app isn't open on hearthpulse.net yet. Everything else works without an account.",
        "INVALID_GRANT" or "INVALID_TOKEN" or "LOGIN_REQUIRED" or "CONSENT_REQUIRED" => "Your session has ended. Sign in again.",
        "STATE_MISMATCH" or "ISSUER_MISMATCH" => "The browser answered for a different sign-in attempt. Press the button to try again.",
        "BAD_RESPONSE" or "BAD_TOKEN" => "hearthpulse.net sent an answer the app can't trust. Try again later.",
        "LISTENER_FAILED" => "The app couldn't receive the answer from the browser. Check that a firewall doesn't block Hearthpulse on 127.0.0.1.",
        "SIGNED_OUT" => "You are signed out.",
        _ => "Something went wrong while signing in. Try again.",
    });
}
