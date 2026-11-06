using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

public sealed partial class AccountService
{
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
                Activity.Failed(SiteService.Account, ex.Code);
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
                Activity.Failed(SiteService.Account, ex.Code);
            }
            catch (AccountException ex)
            {
                // The new pair refreshes but is not good for the profile: give it back instead of leaving it alive.
                Diag.Info($"account: session dropped after /me ({ex.Code})");
                Activity.Failed(SiteService.Account, ex.Code);
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
            Activity.Failed(SiteService.Account, ex.Code);
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
        SetSession(user, newSignIn: sessionExpires is not null);
        Activity.Succeeded(SiteService.Account);

        WatchSessionEnd();
        Changed?.Invoke();
    }

    /// <summary>Drops the session here. Call under the gate.</summary>
    private void Forget()
    {
        Volatile.Write(ref _session, null);
        _accessToken = null;
        _refreshToken = null;
        SessionExpires = null;
        _phaseTimer?.Dispose();
        _phaseTimer = null;
        var had = User is not null;
        User = null;
        _store.Delete();
        UpdateRetry();
        if (had)
        {
            Changed?.Invoke();
        }
    }

    // Called with the token gate held (or while restoring before any token is available).
    private void SetSession(AccountUser user, bool newSignIn)
    {
        // From the first sign-in on, every change of the journal re-checks whether the retry while offline is due.
        if (Interlocked.Exchange(ref _watching, 1) == 0)
        {
            Activity.Changed += UpdateRetry;
        }

        if (user.Id.Length == 0)
        {
            Volatile.Write(ref _session, null);
            return;
        }

        if (newSignIn || Session?.AccountId != user.Id)
        {
            Volatile.Write(ref _session, new AccountSession(user.Id, Interlocked.Increment(ref _sessionGeneration)));
        }

        // The journal's game counters belong to this account: another one signing in starts them over.
        Activity.BindAccount(user.Id);
    }

    /// <summary>
    /// Starts, keeps or stops the retry while offline: signed in and <see cref="AccountLink.Offline"/> schedules a light
    /// <c>/me</c> on the backoff (one at a time); any other state cancels it and starts the backoff over.
    /// </summary>
    private void UpdateRetry()
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var offline = SignedIn && AccountConnection.Of(this, now).Link == AccountLink.Offline;
        lock (_retryGate)
        {
            if (!offline)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
                _retryFailures = 0;
                _nextTry = null;
                return;
            }

            if (_retryTimer is not null || _retrying)
            {
                return;
            }

            var wait = TimeSpan.FromTicks(FirstOfflineRetry.Ticks << Math.Min(_retryFailures, 4));
            wait = wait > MaxOfflineRetry ? MaxOfflineRetry : wait;
            _nextTry = now + wait;
            _retryTimer = Time.CreateTimer(_ => StartRetry(), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void StartRetry()
    {
        lock (_retryGate)
        {
            _retryTimer?.Dispose();
            _retryTimer = null;
            if (_retrying)
            {
                return;
            }

            _retrying = true;
            _retryFailures++;
        }

        PendingRetry = RetryAsync();
    }

    private async Task RetryAsync()
    {
        try
        {
            if (_refreshToken is not null && User is not null)
            {
                Diag.Info("account: no connection, asking the site again");
                LastCheckError = await RefreshAsync(withProfile: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_retryGate)
            {
                _retrying = false;
            }

            UpdateRetry();
        }
    }

    private void WatchSessionEnd()
    {
        _phase = Phase();
        _phaseTimer ??= new Timer(_ => CheckSessionPhase(), null, PhaseCheck, PhaseCheck);
    }

    // 0 = fine, 1 = ending within two days, 2 = ended.
    private int Phase() => SessionExpires is not { } end ? 0 : UtcNow() >= end ? 2 : SessionEndingSoon ? 1 : 0;
}
