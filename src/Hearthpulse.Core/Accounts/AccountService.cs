using System.Text.Json;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.Accounts;

/// <summary>The signed-in Hearthpulse account.</summary>
public sealed record AccountUser(string Id, string Email, string DisplayName, DateTime? CreatedAt);

/// <summary>Why signing in, signing up or refreshing failed, in words the player can act on.</summary>
public sealed record AccountError(string Code, string Message);

/// <summary>
/// Hearthpulse accounts on the Hearthpulse API (contract: docs/accounts-api.md). Keeps the session across restarts:
/// the refresh token is stored encrypted (<see cref="TokenStore"/>); the password is never stored. The access token
/// lives in memory only and is renewed with the refresh token when it runs out.
/// </summary>
public sealed class AccountService
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly HearthpulseApi _api;
    private readonly TokenStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTime _accessExpires;
    private string? _refreshToken;

    public AccountService(HearthpulseApi api, TokenStore? store = null)
    {
        _api = api;
        _store = store ?? new TokenStore(Path.Combine(AppPaths.Root, "account.dat"));
    }

    /// <summary>Raised (on any thread) when the player signs in or out, or a stored session turns out to be gone.</summary>
    public event Action? Changed;

    public AccountUser? User { get; private set; }

    public bool SignedIn => User is not null;

    /// <summary>
    /// Restores the session saved by a previous start. Without network the saved profile is shown as it was; a
    /// session the server rejects is forgotten.
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
        var error = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (error is { Code: not ("OFFLINE" or "SERVER_ERROR" or "RATE_LIMITED" or "NOT_AVAILABLE") })
        {
            Diag.Info($"account: saved session rejected ({error.Code})");
            Forget();
        }
    }

    public Task<AccountError?> SignInAsync(string email, string password, CancellationToken cancellationToken = default) =>
        AuthenticateAsync("v1/accounts/login", new { email = email.Trim(), password }, cancellationToken);

    public Task<AccountError?> SignUpAsync(string email, string password, string displayName, CancellationToken cancellationToken = default) =>
        AuthenticateAsync("v1/accounts/register", new { email = email.Trim(), password, display_name = displayName.Trim() }, cancellationToken);

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var refresh = _refreshToken;
        Forget();
        if (refresh is not null)
        {
            // Best effort: the session is gone locally even if the server cannot be told.
            await _api.PostAsync("v1/accounts/logout", new { refresh_token = refresh }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A valid access token for an authenticated call, renewed when needed; null when signed out or offline.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_accessToken is not null && DateTime.UtcNow < _accessExpires - RefreshMargin)
        {
            return _accessToken;
        }

        return _refreshToken is null || await RefreshAsync(cancellationToken).ConfigureAwait(false) is not null ? null : _accessToken;
    }

    private async Task<AccountError?> AuthenticateAsync(string path, object body, CancellationToken cancellationToken)
    {
        var result = await _api.PostAsync(path, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Ok ? Accept(result.Data) : Describe(result.Error!);
    }

    private async Task<AccountError?> RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_refreshToken is null)
            {
                return new AccountError("SIGNED_OUT", "You are signed out.");
            }

            var result = await _api.PostAsync("v1/accounts/refresh", new { refresh_token = _refreshToken }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Ok ? Accept(result.Data) : Describe(result.Error!);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AccountError? Accept(JsonElement data)
    {
        if (!TryParseSession(data, out var access, out var refresh, out var expiresIn, out var user))
        {
            return new AccountError("BAD_RESPONSE", "The account server sent an unexpected answer. Try again later.");
        }

        _accessToken = access;
        _accessExpires = DateTime.UtcNow.AddSeconds(expiresIn);
        _refreshToken = refresh;
        User = user;
        _store.Save(refresh, user);
        Changed?.Invoke();
        return null;
    }

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

    internal static bool TryParseSession(JsonElement data, out string access, out string refresh, out int expiresIn, out AccountUser user)
    {
        access = refresh = string.Empty;
        expiresIn = 0;
        user = null!;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("access_token", out var a) || a.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("refresh_token", out var r) || r.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("user", out var u) || ParseUser(u) is not { } parsed)
        {
            return false;
        }

        access = a.GetString()!;
        refresh = r.GetString()!;
        expiresIn = data.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) && seconds > 0 ? seconds : 900;
        user = parsed;
        return true;
    }

    internal static AccountUser? ParseUser(JsonElement u)
    {
        if (u.ValueKind != JsonValueKind.Object || !u.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = u.TryGetProperty("id", out var i) ? i.ValueKind == JsonValueKind.String ? i.GetString()! : i.GetRawText() : string.Empty;
        var name = u.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : email.GetString()!;
        DateTime? created = u.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var date) ? date : null;
        return new AccountUser(id, email.GetString()!, name, created);
    }

    /// <summary>Turns an API error into a message for the sign-in form.</summary>
    internal static AccountError Describe(ApiError error) => new(error.Code, error.Code switch
    {
        "OFFLINE" => "Can't reach the Hearthpulse server. Check your connection and try again.",
        "NOT_AVAILABLE" => "Hearthpulse accounts are not open yet. Everything else works without one.",
        "INVALID_CREDENTIALS" => "Wrong email or password.",
        "EMAIL_TAKEN" => "An account with this email already exists. Sign in instead.",
        "WEAK_PASSWORD" => "Choose a longer password (at least 8 characters).",
        "INVALID_TOKEN" or "TOKEN_EXPIRED" => "Your session has ended. Sign in again.",
        "RATE_LIMITED" => "Too many attempts. Wait a minute and try again.",
        "SERVER_ERROR" => "The Hearthpulse server has a problem right now. Try again later.",
        _ => string.IsNullOrWhiteSpace(error.Message) ? "Something went wrong. Try again." : error.Message,
    });
}
