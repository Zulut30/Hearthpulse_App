using System.Buffers.Text;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Accounts;

/// <summary>Endpoints of the identity provider, read from its discovery document.</summary>
public sealed record OidcEndpoints(Uri Authorization, Uri Token, Uri Jwks, Uri? UserInfo, Uri? Revocation);

/// <summary>An answer of the token endpoint.</summary>
public sealed record OidcTokens(string AccessToken, string? RefreshToken, string? IdToken, int ExpiresIn);

/// <summary>
/// A failure inside the accounts code: an OAuth error from the provider upper-cased (<c>INVALID_GRANT</c>,
/// <c>ACCESS_DENIED</c>...) or one of ours (<c>OFFLINE</c>, <c>SERVER_ERROR</c>, <c>BAD_RESPONSE</c>, <c>BAD_TOKEN</c>...).
/// <see cref="AccountService"/> turns it into an <see cref="AccountError"/>.
/// </summary>
internal sealed class AccountException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

/// <summary>
/// OpenID Connect client for the HeartPulse site's identity provider (contract: docs/accounts-api.md). A public
/// native client: no secret, authorization code + PKCE, refresh tokens that rotate on every use. Endpoints come from
/// discovery and are cached; the id_token signature is checked against the provider's JWKS (RS256 or ES256).
/// </summary>
public sealed class OidcClient(HttpClient http, string? issuer = null)
{
    public const string DefaultIssuer = "https://hearthpulse.net/identity";

    public const string ClientId = "hearthpulse-desktop";

    public const string Scope = "openid profile offline_access";

    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromHours(12);

    // Clocks of players' PCs drift; two minutes is the usual allowance for exp/iat.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OidcEndpoints? _endpoints;
    private DateTime _endpointsUntil;
    private List<JsonElement>? _keys;

    /// <summary>The provider: <paramref name="issuer"/>, else the HEARTHPULSE_ISSUER variable (for a test site), else hearthpulse.net.</summary>
    public string Issuer { get; } = (issuer is { Length: > 0 } ? issuer
        : Environment.GetEnvironmentVariable("HEARTHPULSE_ISSUER") is { Length: > 0 } env ? env
        : DefaultIssuer).TrimEnd('/');

    /// <summary>The player's profile page on the site that runs the provider.</summary>
    public Uri ProfileUrl => new(new Uri(Issuer).GetLeftPart(UriPartial.Authority) + "/profile");

    /// <summary>The provider's endpoints, from <c>{issuer}/.well-known/openid-configuration</c> (cached for 12 hours).</summary>
    public async Task<OidcEndpoints> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_endpoints is not null && DateTime.UtcNow < _endpointsUntil)
            {
                return _endpoints;
            }

            using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Issuer + "/.well-known/openid-configuration"), cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            // A document for another issuer would let a look-alike provider mint tokens we accept (OIDC Discovery 4.3).
            if (Text(root, "issuer") != Issuer)
            {
                throw new AccountException("BAD_RESPONSE", "discovery issuer mismatch");
            }

            _endpoints = new OidcEndpoints(
                Endpoint(root, "authorization_endpoint") ?? throw new AccountException("BAD_RESPONSE", "no authorization_endpoint"),
                Endpoint(root, "token_endpoint") ?? throw new AccountException("BAD_RESPONSE", "no token_endpoint"),
                Endpoint(root, "jwks_uri") ?? throw new AccountException("BAD_RESPONSE", "no jwks_uri"),
                Endpoint(root, "userinfo_endpoint"),
                Endpoint(root, "revocation_endpoint"));
            _endpointsUntil = DateTime.UtcNow + DiscoveryLifetime;
            _keys = null;
            return _endpoints;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The browser URL that starts a sign-in.</summary>
    public static Uri AuthorizationUrl(OidcEndpoints endpoints, string redirectUri, string codeChallenge, string state, string nonce)
    {
        (string Name, string Value)[] parameters =
        [
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", redirectUri),
            ("scope", Scope),
            ("state", state),
            ("nonce", nonce),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
            // OIDC Core 11: offline_access is only honoured together with prompt=consent.
            ("prompt", "consent"),
        ];
        var query = string.Join('&', parameters.Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value)));
        var url = endpoints.Authorization.AbsoluteUri;
        return new Uri(url + (endpoints.Authorization.Query.Length > 0 ? "&" : "?") + query);
    }

    public Task<OidcTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken = default) =>
        TokenAsync([("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri), ("code_verifier", codeVerifier)], cancellationToken);

    /// <summary>New tokens for a refresh token. The provider rotates it: the answer carries the one to keep.</summary>
    public Task<OidcTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refreshToken)], cancellationToken);

    /// <summary>
    /// The profile behind <paramref name="tokens"/>: <c>sub</c> from the verified id_token, <c>name</c> from userinfo
    /// (in the code flow the provider puts profile claims there, not in the id_token). <paramref name="nonce"/> is the
    /// sign-in's nonce, or null after a refresh (refreshed id_tokens carry none).
    /// </summary>
    internal async Task<AccountUser> ProfileAsync(OidcTokens tokens, string? nonce, CancellationToken cancellationToken)
    {
        string? subject = null;
        string? name = null;
        if (tokens.IdToken is not null)
        {
            (subject, name) = await ValidateIdTokenAsync(tokens.IdToken, nonce, cancellationToken).ConfigureAwait(false);
        }
        else if (nonce is not null)
        {
            throw new AccountException("BAD_RESPONSE", "no id_token");
        }

        try
        {
            var (infoSubject, infoName) = await GetUserInfoAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
            if (subject is not null && infoSubject != subject)
            {
                throw new AccountException("BAD_TOKEN", "userinfo sub differs from id_token");
            }

            subject = infoSubject;
            name = infoName ?? name;
        }
        catch (AccountException ex) when (subject is not null && ex.Code != "BAD_TOKEN")
        {
            Diag.Info($"account: userinfo failed ({ex.Code}), using the id_token");
        }

        return new AccountUser(subject, string.IsNullOrWhiteSpace(name) ? "HeartPulse player" : name.Trim());
    }

    public async Task<(string Subject, string? Name)> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var endpoints = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (endpoints.UserInfo is null)
        {
            throw new AccountException("BAD_RESPONSE", "no userinfo_endpoint");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, endpoints.UserInfo);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var doc = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (Text(doc.RootElement, "sub") ?? throw new AccountException("BAD_RESPONSE", "userinfo without sub"), Text(doc.RootElement, "name"));
    }

    /// <summary>Revokes <paramref name="token"/> (RFC 7009) when the provider has a revocation endpoint.</summary>
    public async Task RevokeAsync(string token, string tokenTypeHint, CancellationToken cancellationToken = default)
    {
        var endpoints = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (endpoints.Revocation is null)
        {
            return;
        }

        try
        {
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoints.Revocation)
            {
                Content = Form([("token", token), ("token_type_hint", tokenTypeHint)]),
            }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"account: revocation answered {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AccountException("OFFLINE", ex.Message);
        }
    }

    /// <summary>
    /// Checks an id_token: signature by the JWKS key named in its header, then <c>iss</c>, <c>aud</c>/<c>azp</c>,
    /// <c>exp</c>, <c>iat</c> and <c>nonce</c>. Returns its <c>sub</c> and <c>name</c>.
    /// </summary>
    public async Task<(string Subject, string? Name)> ValidateIdTokenAsync(string idToken, string? nonce, CancellationToken cancellationToken = default)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new AccountException("BAD_TOKEN", "id_token is not a signed JWT");
        }

        JsonDocument header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]));
            payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new AccountException("BAD_TOKEN", "id_token does not parse");
        }

        using (header)
        using (payload)
        {
            var alg = Text(header.RootElement, "alg");
            var kid = Text(header.RootElement, "kid");
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!await VerifyAsync(kid, alg, signed, signature, cancellationToken).ConfigureAwait(false))
            {
                throw new AccountException("BAD_TOKEN", $"id_token signature ({alg ?? "no alg"}) does not verify");
            }

            var claims = payload.RootElement;
            var now = DateTimeOffset.UtcNow;
            if (Text(claims, "iss") != Issuer)
            {
                throw new AccountException("BAD_TOKEN", "id_token iss");
            }

            if (!ForUs(claims))
            {
                throw new AccountException("BAD_TOKEN", "id_token aud");
            }

            if (!(Seconds(claims, "exp") is { } exp && now - ClockSkew < exp) || Seconds(claims, "iat") is { } iat && iat > now + ClockSkew)
            {
                throw new AccountException("BAD_TOKEN", "id_token expired or not yet valid");
            }

            if (nonce is not null && Text(claims, "nonce") != nonce)
            {
                throw new AccountException("BAD_TOKEN", "id_token nonce");
            }

            return (Text(claims, "sub") is { Length: > 0 } sub ? sub : throw new AccountException("BAD_TOKEN", "id_token sub"), Text(claims, "name"));
        }
    }

    private async Task<bool> VerifyAsync(string? kid, string? alg, byte[] signed, byte[] signature, CancellationToken cancellationToken)
    {
        // A key we do not know yet may be a freshly rotated one: reload the JWKS once before giving up.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var keys = _keys;
            if (keys is null || attempt == 1)
            {
                var endpoints = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Get, endpoints.Jwks), cancellationToken).ConfigureAwait(false);
                keys = doc.RootElement.TryGetProperty("keys", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(k => k.Clone()).ToList()
                    : [];
                _keys = keys;
            }

            var candidates = keys.Where(k => (kid is null || Text(k, "kid") == kid) && Text(k, "use") is null or "sig").ToList();
            if (candidates.Count > 0)
            {
                return candidates.Any(k => Verify(k, alg, signed, signature));
            }
        }

        return false;
    }

    internal static bool Verify(JsonElement jwk, string? alg, byte[] signed, byte[] signature)
    {
        if (Text(jwk, "alg") is { } keyAlg && keyAlg != alg)
        {
            return false;
        }

        try
        {
            switch (alg)
            {
                case "RS256" when Text(jwk, "kty") == "RSA":
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportParameters(new RSAParameters { Modulus = Bytes(jwk, "n"), Exponent = Bytes(jwk, "e") });
                        return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }

                case "ES256" when Text(jwk, "kty") == "EC" && Text(jwk, "crv") == "P-256":
                    using (var ec = ECDsa.Create(new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint { X = Bytes(jwk, "x"), Y = Bytes(jwk, "y") },
                    }))
                    {
                        // JWS ES256 signatures are r||s, which is .NET's default (IEEE P1363) format.
                        return ec.VerifyData(signed, signature, HashAlgorithmName.SHA256);
                    }

                default:
                    // "none", HMAC (a public client has no secret) and anything unexpected are refused.
                    return false;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static bool ForUs(JsonElement claims)
    {
        if (!claims.TryGetProperty("aud", out var aud))
        {
            return false;
        }

        if (aud.ValueKind == JsonValueKind.String)
        {
            return aud.GetString() == ClientId;
        }

        var audiences = aud.ValueKind == JsonValueKind.Array
            ? aud.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).ToList()
            : [];
        // With several audiences the token must name us as the party it was issued to (OIDC Core 3.1.3.7).
        return audiences.Contains(ClientId) && (audiences.Count == 1 || Text(claims, "azp") == ClientId);
    }

    private async Task<OidcTokens> TokenAsync((string, string)[] fields, CancellationToken cancellationToken)
    {
        var endpoints = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoints.Token) { Content = Form(fields) }, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (Text(root, "access_token") is not { Length: > 0 } access || !string.Equals(Text(root, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new AccountException("BAD_RESPONSE", "token answer without a bearer access_token");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var seconds) && seconds > 0 ? seconds : 300;
        return new OidcTokens(access, Text(root, "refresh_token"), Text(root, "id_token"), expiresIn);
    }

    private static FormUrlEncodedContent Form((string Name, string Value)[] fields) =>
        new(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)).Append(KeyValuePair.Create("client_id", ClientId)));

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

    /// <summary>A JSON object on success; otherwise the OAuth <c>error</c> (RFC 6749 5.2) or a code for the status.</summary>
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

        var error = doc is null ? null : Text(doc.RootElement, "error");
        var description = doc is null ? null : Text(doc.RootElement, "error_description");
        doc?.Dispose();
        throw new AccountException(error is { Length: > 0 } ? error.ToUpperInvariant() : status switch
        {
            404 => "NOT_AVAILABLE",
            429 => "RATE_LIMITED",
            >= 500 => "SERVER_ERROR",
            _ => "BAD_RESPONSE",
        }, description ?? $"HTTP {status.ToString(CultureInfo.InvariantCulture)}");
    }

    private Uri? Endpoint(JsonElement root, string name)
    {
        // Endpoints use the issuer's scheme: https in production, plain http only for a provider on this machine.
        return Text(root, name) is { } value && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == new Uri(Issuer).Scheme && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)
            ? uri
            : null;
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Seconds(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static byte[] Bytes(JsonElement jwk, string name) =>
        Base64Url.DecodeFromChars(Text(jwk, name) ?? throw new FormatException($"JWK without {name}"));
}
