using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hearthpulse.Core.Accounts;

/// <summary>A device authorization (RFC 8628 3.2): the code the player confirms on the site.</summary>
public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, Uri? VerificationUriComplete, int ExpiresIn, int Interval)
{
    /// <summary>The page to type the code on, without the scheme: "hearthpulse.net/connect".</summary>
    public string DisplayUri => (VerificationUri.Host + VerificationUri.AbsolutePath).TrimEnd('/');
}

/// <summary>An answer of the token endpoint.</summary>
public sealed record AccountTokens(string AccessToken, string? RefreshToken, int ExpiresIn);

/// <summary>
/// A failure inside the accounts code: an OAuth error upper-cased (<c>AUTHORIZATION_PENDING</c>, <c>SLOW_DOWN</c>,
/// <c>ACCESS_DENIED</c>, <c>EXPIRED_TOKEN</c>, <c>INVALID_GRANT</c>...), an API error code
/// (<c>INVALID_ACCESS_TOKEN</c>...) or one of ours (<c>OFFLINE</c>, <c>SERVER_ERROR</c>, <c>BAD_RESPONSE</c>).
/// <see cref="AccountService"/> turns it into an <see cref="AccountError"/>.
/// </summary>
internal sealed class AccountException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The HeartPulse site's application API (contract: docs/accounts-api.md): OAuth 2.0 device authorization grant for
/// the public client <c>manacost-tracker</c>, refresh tokens that rotate on every use, revocation and <c>/me</c>.
/// Bodies are form-encoded, answers JSON.
/// </summary>
public sealed class AccountClient(HttpClient http, string? baseUrl = null)
{
    public const string DefaultBaseUrl = "https://hearthpulse.net/api/v1/";

    public const string ClientId = "manacost-tracker";

    public const string Scope = "profile.read subscription.read catalog.read images.read statistics.read tracker.write";

    public const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>The API: <paramref name="baseUrl"/>, else the HEARTHPULSE_SITE_API variable (for a test site), else hearthpulse.net.</summary>
    public Uri BaseUri { get; } = new((baseUrl is { Length: > 0 } ? baseUrl
        : Environment.GetEnvironmentVariable("HEARTHPULSE_SITE_API") is { Length: > 0 } env ? env
        : DefaultBaseUrl).TrimEnd('/') + "/");

    /// <summary>The account page on the site, for when <c>/me</c> has not named one.</summary>
    public Uri DefaultProfileUrl => new(BaseUri.GetLeftPart(UriPartial.Authority) + "/profile/");

    /// <summary><c>POST /oauth/device/code</c>: a new device code and the user code to show.</summary>
    public async Task<DeviceCode> BeginAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await PostAsync("oauth/device/code", [("client_id", ClientId), ("scope", Scope)], cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (Text(root, "device_code") is not { Length: > 0 } code || Text(root, "user_code") is not { Length: > 0 } userCode
            || Https(Text(root, "verification_uri")) is not { } verification)
        {
            throw new AccountException("BAD_RESPONSE", "device authorization without code or https verification_uri");
        }

        return new DeviceCode(code, userCode, verification, Https(Text(root, "verification_uri_complete")),
            Number(root, "expires_in") ?? 600, Number(root, "interval") ?? 5);
    }

    /// <summary>
    /// One poll of <c>POST /oauth/token</c> with the device code. While the player has not confirmed it throws
    /// <c>AUTHORIZATION_PENDING</c> or <c>SLOW_DOWN</c>; the caller waits and asks again.
    /// </summary>
    public Task<AccountTokens> PollAsync(DeviceCode device, CancellationToken cancellationToken = default) =>
        TokenAsync([("grant_type", DeviceGrant), ("device_code", device.Code)], cancellationToken);

    /// <summary>New tokens for a refresh token. The site rotates it: the answer carries the one to keep.</summary>
    public Task<AccountTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refreshToken)], cancellationToken);

    /// <summary><c>POST /oauth/revoke</c>: ends the refresh token's whole token family on the site.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var _ = await PostAsync("oauth/revoke", [("token", refreshToken)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>GET /me</c>: the profile and paid access behind an access token.</summary>
    public async Task<(AccountUser User, Uri? ProfileUrl)> GetMeAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "me"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var doc = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ParseMe(doc.RootElement);
    }

    internal static (AccountUser User, Uri? ProfileUrl) ParseMe(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || Text(user, "id") is not { Length: > 0 } id)
        {
            throw new AccountException("BAD_RESPONSE", "/me without user.id");
        }

        var name = Text(user, "name") is { } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "HeartPulse player";
        var subscription = root.TryGetProperty("subscription", out var s) ? Entitlements.Parse(s) : Entitlements.None;
        return (new AccountUser(id, name, subscription), Https(Text(user, "profileUrl")));
    }

    private async Task<AccountTokens> TokenAsync((string, string)[] fields, CancellationToken cancellationToken)
    {
        using var doc = await PostAsync("oauth/token", [.. fields, ("client_id", ClientId)], cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (Text(root, "access_token") is not { Length: > 0 } access || !string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new AccountException("BAD_RESPONSE", "token answer without a bearer access_token");
        }

        return new AccountTokens(access, Text(root, "refresh_token"), Number(root, "expires_in") ?? 900);
    }

    private Task<JsonDocument> PostAsync(string path, (string Name, string Value)[] fields, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, path))
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))),
        }, cancellationToken);

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int status;
        string body;
        try
        {
            using (request)
            using (var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AccountException("OFFLINE", ex.Message);
        }

        return Parse(status, body);
    }

    /// <summary>
    /// A JSON object on success. Errors come as OAuth <c>{"error": "slow_down"}</c> (RFC 6749 5.2) or as the API's
    /// <c>{"error": {"code", "message"}}</c>; without either the status decides.
    /// </summary>
    internal static JsonDocument Parse(int status, string body)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
        }

        if (status is >= 200 and < 300 && doc?.RootElement.ValueKind == JsonValueKind.Object)
        {
            return doc;
        }

        string? code = null, detail = null;
        if (doc?.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
        {
            code = error.ValueKind == JsonValueKind.String ? error.GetString() : Text(error, "code");
            detail = Text(doc.RootElement, "error_description") ?? Text(error, "message");
        }

        doc?.Dispose();
        throw new AccountException(code is { Length: > 0 } ? code.ToUpperInvariant() : status switch
        {
            404 => "NOT_AVAILABLE",
            429 => "RATE_LIMITED",
            >= 500 => "SERVER_ERROR",
            _ => "BAD_RESPONSE",
        }, detail ?? $"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    // Pages the app opens or links to must be https (a plain-http code page could be swapped on the way); plain http
    // only for a test site on this machine.
    private static Uri? Https(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri
            : null;
}
