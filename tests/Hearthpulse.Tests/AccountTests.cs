using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Accounts;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Signing in through hearthpulse.net (OIDC, PKCE, loopback redirect) without the network: a fake provider answers
/// discovery, JWKS, token, userinfo and revocation, and a fake browser follows the authorization URL by calling the
/// app's loopback listener itself.
/// </summary>
public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hp-account-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly TokenStore _store;

    public AccountTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TokenStore(Path.Combine(_dir, "account.dat"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private OidcClient Oidc() => new(new HttpClient(_provider), FakeProvider.Issuer);

    private AccountService Service(FakeBrowser? browser = null) =>
        new(Oidc(), _store, (browser ?? new FakeBrowser(_provider)).Open) { SignInTimeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public void Pkce_verifier_is_random_and_the_challenge_is_s256()
    {
        var verifier = Pkce.NewVerifier();

        Assert.Equal(43, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.NotEqual(verifier, Pkce.NewVerifier());
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task Authorization_url_asks_for_code_with_pkce_state_and_nonce()
    {
        var endpoints = await Oidc().DiscoverAsync();

        var query = FakeProvider.Query(OidcClient.AuthorizationUrl(endpoints, "http://127.0.0.1:5000/callback", "chal", "st", "no").Query);

        Assert.Equal("https://id.test/identity/auth", endpoints.Authorization.AbsoluteUri);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("hearthpulse-desktop", query["client_id"]);
        Assert.Equal("http://127.0.0.1:5000/callback", query["redirect_uri"]);
        Assert.Equal("openid profile offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(("chal", "st", "no"), (query["code_challenge"], query["state"], query["nonce"]));
    }

    [Theory]
    [InlineData("code=abc&state=s1", null)]
    [InlineData("code=abc&state=other", "STATE_MISMATCH")]
    [InlineData("error=access_denied&state=other", "STATE_MISMATCH")]
    [InlineData("code=abc", "STATE_MISMATCH")]
    [InlineData("error=access_denied&error_description=End-User+aborted&state=s1", "ACCESS_DENIED")]
    [InlineData("code=abc&state=s1&iss=https%3A%2F%2Fevil.test%2Fidentity", "ISSUER_MISMATCH")]
    [InlineData("code=abc&state=s1&iss=https%3A%2F%2Fid.test%2Fidentity", null)]
    [InlineData("state=s1", "BAD_RESPONSE")]
    public void Callback_checks_state_first_then_the_answer(string query, string? error)
    {
        if (error is null)
        {
            Assert.Equal("abc", BrowserSignIn.ParseCallback(query, "s1", FakeProvider.Issuer));
            return;
        }

        var ex = Assert.Throws<AccountException>(() => BrowserSignIn.ParseCallback(query, "s1", FakeProvider.Issuer));
        Assert.Equal(error, ex.Code);
    }

    [Fact]
    public async Task Browser_sign_in_exchanges_the_code_and_keeps_the_session()
    {
        var browser = new FakeBrowser(_provider);
        var account = Service(browser);

        Assert.Null(await account.SignInAsync());

        Assert.Equal(new AccountUser("user-42", "Anna"), account.User);
        Assert.Contains("Вход выполнен", await browser.Page!);
        var exchange = FakeProvider.Query(_provider.Bodies("/identity/token").Single());
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(browser.RedirectUri, exchange["redirect_uri"]);
        Assert.Equal(browser.Challenge, Pkce.Challenge(exchange["code_verifier"]));
        Assert.Equal("hearthpulse-desktop", exchange["client_id"]);
        Assert.Matches("^http://127\\.0\\.0\\.1:[0-9]+/callback$", browser.RedirectUri);
        Assert.Equal("acc-1", await account.GetAccessTokenAsync());
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);
        Assert.Equal("Anna", _store.Load()!.Value.User.DisplayName);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_the_new_one_is_saved()
    {
        _provider.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync();

        Assert.Equal("acc-2", await account.GetAccessTokenAsync());
        Assert.Equal("ref-2", _store.Load()!.Value.RefreshToken);
        Assert.Equal("acc-3", await account.GetAccessTokenAsync());

        var refreshes = _provider.Bodies("/identity/token").Skip(1).Select(b => FakeProvider.Query(b)["refresh_token"]).ToList();
        Assert.Equal(new[] { "ref-1", "ref-2" }, refreshes);
        Assert.Equal("ref-3", _store.Load()!.Value.RefreshToken);
    }

    [Fact]
    public async Task Restore_refreshes_the_saved_session_and_picks_up_a_new_name()
    {
        _provider.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("user-42", "Old name"));
        _provider.Name = "Anna";

        var account = Service();
        await account.RestoreAsync();

        Assert.True(account.SignedIn);
        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);
    }

    [Fact]
    public async Task Restore_keeps_an_offline_session_but_forgets_a_rejected_one()
    {
        _store.Save("ref-gone", new AccountUser("user-42", "Anna"));

        _provider.Offline = true;
        var offline = Service();
        await offline.RestoreAsync();
        Assert.True(offline.SignedIn);
        Assert.NotNull(_store.Load());

        _provider.Offline = false;
        var rejected = Service();
        await rejected.RestoreAsync();
        Assert.False(rejected.SignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Sign_out_revokes_the_refresh_token_and_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync();

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load());
        Assert.Null(await account.GetAccessTokenAsync());
        var revoke = FakeProvider.Query(_provider.Bodies("/identity/token/revocation").Single());
        Assert.Equal(("ref-1", "refresh_token", "hearthpulse-desktop"), (revoke["token"], revoke["token_type_hint"], revoke["client_id"]));
    }

    [Fact]
    public async Task Sign_out_offline_still_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync();
        _provider.Offline = true;

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task Declining_in_the_browser_explains_why()
    {
        var browser = new FakeBrowser(_provider) { Answer = state => $"error=access_denied&state={state}" };
        var account = Service(browser);

        var error = await account.SignInAsync();

        Assert.Equal("ACCESS_DENIED", error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(account.SignedIn);
        Assert.Contains("Войти не получилось", await browser.Page!);
        Assert.Empty(_provider.Bodies("/identity/token"));
    }

    [Fact]
    public async Task A_redirect_for_another_attempt_is_refused()
    {
        var browser = new FakeBrowser(_provider) { Answer = _ => "code=code-1&state=forged" };

        var error = await Service(browser).SignInAsync();

        Assert.Equal("STATE_MISMATCH", error!.Code);
        Assert.Empty(_provider.Bodies("/identity/token"));
    }

    [Fact]
    public async Task Waiting_ends_on_cancel_and_on_timeout()
    {
        var silent = new FakeBrowser(_provider) { Answer = null };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.Equal("CANCELLED", (await Service(silent).SignInAsync(cancel.Token))!.Code);

        var impatient = new AccountService(Oidc(), _store, silent.Open) { SignInTimeout = TimeSpan.FromMilliseconds(200) };
        Assert.Equal("TIMEOUT", (await impatient.SignInAsync())!.Code);
    }

    [Fact]
    public async Task An_unregistered_client_says_sign_in_is_not_open_yet()
    {
        var browser = new FakeBrowser(_provider) { Answer = state => $"error=unauthorized_client&state={state}" };

        var error = await Service(browser).SignInAsync();

        Assert.Equal("UNAUTHORIZED_CLIENT", error!.Code);
        Assert.Contains("isn't open", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Id_tokens_are_checked_for_signature_issuer_audience_expiry_and_nonce()
    {
        var oidc = Oidc();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object Claims(string iss = FakeProvider.Issuer, object? aud = null, long? exp = null, string nonce = "n1") =>
            new { iss, aud = aud ?? "hearthpulse-desktop", sub = "user-42", exp = exp ?? now + 300, iat = now, nonce };

        Assert.Equal("user-42", (await oidc.ValidateIdTokenAsync(_provider.Sign(Claims()), "n1")).Subject);
        using var stranger = RSA.Create(2048);
        await AssertBad(FakeProvider.Sign(Claims(), stranger, "k1"));
        await AssertBad(_provider.Sign(Claims(iss: "https://evil.test/identity")));
        await AssertBad(_provider.Sign(Claims(aud: "someone-else")));
        await AssertBad(_provider.Sign(Claims(aud: new[] { "hearthpulse-desktop", "other" })));
        await AssertBad(_provider.Sign(Claims(exp: now - 600)));
        await AssertBad(_provider.Sign(Claims(nonce: "n2")));
        await AssertBad(Unsigned(Claims()));
        await AssertBad("not-a-jwt");

        async Task AssertBad(string token) =>
            Assert.Equal("BAD_TOKEN", (await Assert.ThrowsAsync<AccountException>(() => oidc.ValidateIdTokenAsync(token, "n1"))).Code);

        static string Unsigned(object claims) =>
            FakeProvider.Encode(new { alg = "none" }) + "." + FakeProvider.Encode(claims) + ".";
    }

    [Fact]
    public async Task Discovery_is_cached_and_must_name_the_issuer()
    {
        var oidc = Oidc();
        await oidc.DiscoverAsync();
        await oidc.DiscoverAsync();
        Assert.Single(_provider.Bodies("/identity/.well-known/openid-configuration"));

        var lookalike = new OidcClient(new HttpClient(_provider), "https://id.test/identity2");
        Assert.Equal("NOT_AVAILABLE", (await Assert.ThrowsAsync<AccountException>(() => lookalike.DiscoverAsync())).Code);
        _provider.DiscoveryIssuer = "https://evil.test/identity";
        var mismatched = new OidcClient(new HttpClient(_provider), FakeProvider.Issuer);
        Assert.Equal("BAD_RESPONSE", (await Assert.ThrowsAsync<AccountException>(() => mismatched.DiscoverAsync())).Code);
    }

    [Fact]
    public void Profile_link_points_at_the_site()
    {
        Assert.Equal("https://hearthpulse.net/profile", new OidcClient(new HttpClient(), OidcClient.DefaultIssuer).ProfileUrl.AbsoluteUri);
    }

    /// <summary>Follows the authorization URL like a browser where the player at once agrees (or as told by <see cref="Answer"/>).</summary>
    private sealed class FakeBrowser(FakeProvider provider)
    {
        /// <summary>The redirect query for a given state; null = the player never comes back.</summary>
        public Func<string, string>? Answer { get; init; } = state => $"code={provider.NextCode}&state={state}&iss={Uri.EscapeDataString(FakeProvider.Issuer)}";

        public string RedirectUri { get; private set; } = string.Empty;

        public string Challenge { get; private set; } = string.Empty;

        public Task<string>? Page { get; private set; }

        public bool Open(Uri url)
        {
            var query = FakeProvider.Query(url.Query);
            RedirectUri = query["redirect_uri"];
            Challenge = query["code_challenge"];
            provider.Authorize(query);
            if (Answer is not null)
            {
                var target = RedirectUri + "?" + Answer(query["state"]);
                Page = Task.Run(async () =>
                {
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                    // A stray request first: the listener must answer 404 and keep waiting for /callback.
                    using (await http.GetAsync(RedirectUri.Replace("/callback", "/favicon.ico", StringComparison.Ordinal)))
                    {
                    }

                    return await http.GetStringAsync(target);
                });
            }

            return true;
        }
    }

    /// <summary>The HeartPulse provider in miniature, answering by request path.</summary>
    private sealed class FakeProvider : HttpMessageHandler
    {
        public const string Issuer = "https://id.test/identity";
        private readonly RSA _key = RSA.Create(2048);
        private readonly List<(string Path, string Body)> _requests = [];
        private readonly HashSet<string> _refreshTokens = [];
        private readonly HashSet<string> _accessTokens = [];
        private string? _nonce;
        private string? _challenge;
        private int _issued;

        public bool Offline { get; set; }

        public int ExpiresIn { get; set; } = 300;

        public string Name { get; set; } = "Anna";

        public string DiscoveryIssuer { get; set; } = Issuer;

        public string NextCode => "code-1";

        public IEnumerable<string> Bodies(string path)
        {
            lock (_requests)
            {
                return _requests.Where(r => r.Path == path).Select(r => r.Body).ToList();
            }
        }

        public void Authorize(Dictionary<string, string> query) => (_nonce, _challenge) = (query["nonce"], query["code_challenge"]);

        /// <summary>Makes <paramref name="refreshToken"/> one the provider accepts (a session from an earlier start).</summary>
        public void Issue(string refreshToken) => _refreshTokens.Add(refreshToken);

        public string Sign(object claims) => Sign(claims, _key, "k1");

        public static string Sign(object claims, RSA key, string kid)
        {
            var signed = Encode(new { alg = "RS256", kid, typ = "JWT" }) + "." + Encode(claims);
            return signed + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

        public static Dictionary<string, string> Query(string query) =>
            query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add((path, body));
            }

            var form = Query(body);
            var p = _key.ExportParameters(false);
            return path switch
            {
                "/identity/.well-known/openid-configuration" => Json(200, new
                {
                    issuer = DiscoveryIssuer,
                    authorization_endpoint = Issuer + "/auth",
                    token_endpoint = Issuer + "/token",
                    jwks_uri = Issuer + "/jwks",
                    userinfo_endpoint = Issuer + "/me",
                    revocation_endpoint = Issuer + "/token/revocation",
                }),
                "/identity/jwks" => Json(200, new
                {
                    keys = new[] { new { kty = "RSA", kid = "k1", use = "sig", alg = "RS256", n = Base64Url.EncodeToString(p.Modulus), e = Base64Url.EncodeToString(p.Exponent) } },
                }),
                "/identity/token" => Token(form),
                "/identity/me" when request.Headers.Authorization?.Parameter is { } bearer && _accessTokens.Contains(bearer) =>
                    Json(200, new { sub = "user-42", name = Name }),
                "/identity/me" => Json(401, new { error = "invalid_token" }),
                "/identity/token/revocation" => Revoke(form),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Token(Dictionary<string, string> form)
        {
            string? nonce = null;
            switch (form.GetValueOrDefault("grant_type"))
            {
                case "authorization_code" when form.GetValueOrDefault("code") == NextCode
                    && form.TryGetValue("code_verifier", out var verifier) && Pkce.Challenge(verifier) == _challenge:
                    nonce = _nonce;
                    break;
                case "refresh_token" when form.TryGetValue("refresh_token", out var refresh) && _refreshTokens.Remove(refresh):
                    break;
                default:
                    return Json(400, new { error = "invalid_grant", error_description = "grant request is invalid" });
            }

            var n = ++_issued;
            _refreshTokens.Add($"ref-{n}");
            _accessTokens.Add($"acc-{n}");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var idToken = Sign(new Dictionary<string, object?>
            {
                ["iss"] = Issuer, ["aud"] = "hearthpulse-desktop", ["sub"] = "user-42", ["iat"] = now, ["exp"] = now + 300, ["nonce"] = nonce,
            }.Where(c => c.Value is not null).ToDictionary());
            return Json(200, new { access_token = $"acc-{n}", token_type = "Bearer", expires_in = ExpiresIn, refresh_token = $"ref-{n}", id_token = idToken, scope = OidcClient.Scope });
        }

        private HttpResponseMessage Revoke(Dictionary<string, string> form)
        {
            _refreshTokens.Remove(form.GetValueOrDefault("token") ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(int status, object value) =>
            new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
