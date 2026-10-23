using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Accounts;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Signing in with a hearthpulse.net account through the OAuth device flow, without the network: a fake site answers
/// device/code, token, revoke and /me, and the polling delay is recorded instead of waited.
/// </summary>
public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hp-account-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSite _site = new();
    private readonly TokenStore _store;
    private readonly List<TimeSpan> _delays = [];
    private readonly List<Uri> _opened = [];

    public AccountTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new TokenStore(Path.Combine(_dir, "account.dat"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AccountClient Client() => new(new HttpClient(_site), FakeSite.Base);

    private AccountService Service() => new(Client(), _store, url => { _opened.Add(url); return true; })
    {
        Delay = (wait, token) =>
        {
            _delays.Add(wait);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        },
    };

    private static double[] Seconds(IEnumerable<TimeSpan> waits) => waits.Select(w => w.TotalSeconds).ToArray();

    [Fact]
    public async Task Device_flow_shows_the_code_waits_for_confirmation_and_reads_the_profile()
    {
        _site.Polls.Enqueue("authorization_pending");
        _site.Polls.Enqueue("authorization_pending");
        var account = Service();
        DeviceCode? shown = null;

        Assert.Null(await account.SignInAsync(code => shown = code));

        Assert.Equal("ABCD-EFGH", shown!.UserCode);
        Assert.Equal("site.test/connect", shown.DisplayUri);
        Assert.Equal("https://site.test/connect/?user_code=ABCD-EFGH", Assert.Single(_opened).AbsoluteUri);
        Assert.Equal(new[] { 5.0, 5, 5 }, Seconds(_delays));
        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.Equal("u-42", account.User.Id);
        Assert.True(account.Entitlements.Has(Entitlements.Battlegrounds));
        Assert.Equal("https://site.test/profile/", account.ProfileUrl.AbsoluteUri);
        Assert.Equal("acc-1", await account.GetAccessTokenAsync());
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);

        var begin = FakeSite.Form(_site.Bodies("oauth/device/code").Single());
        Assert.Equal("manacost-tracker", begin["client_id"]);
        Assert.Equal("profile.read subscription.read catalog.read images.read statistics.read tracker.write", begin["scope"]);
        var poll = FakeSite.Form(_site.Bodies("oauth/token").Last());
        Assert.Equal(("urn:ietf:params:oauth:grant-type:device_code", "mca_device_1", "manacost-tracker"),
            (poll["grant_type"], poll["device_code"], poll["client_id"]));
    }

    [Fact]
    public async Task Slow_down_adds_five_seconds_to_the_interval()
    {
        _site.Polls.Enqueue("slow_down");
        _site.Polls.Enqueue("authorization_pending");
        _site.Polls.Enqueue("slow_down");

        Assert.Null(await Service().SignInAsync(_ => { }));

        Assert.Equal(new[] { 5.0, 10, 10, 15 }, Seconds(_delays));
    }

    [Fact]
    public async Task A_short_outage_while_waiting_is_ridden_out()
    {
        _site.Polls.Enqueue("offline");
        _site.Polls.Enqueue("authorization_pending");

        var account = Service();

        Assert.Null(await account.SignInAsync(_ => { }));
        Assert.True(account.SignedIn);
    }

    [Theory]
    [InlineData("access_denied", "ACCESS_DENIED")]
    [InlineData("expired_token", "EXPIRED_TOKEN")]
    [InlineData("invalid_grant", "INVALID_GRANT")]
    public async Task A_declined_or_expired_code_explains_why(string answer, string code)
    {
        _site.Polls.Enqueue("authorization_pending");
        _site.Polls.Enqueue(answer);
        var account = Service();

        var error = await account.SignInAsync(_ => { });

        Assert.Equal(code, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(account.SignedIn);
        Assert.Null(_store.Load());
        Assert.Empty(_site.Bodies("me"));
    }

    [Fact]
    public async Task Cancel_stops_waiting()
    {
        using var cancel = new CancellationTokenSource();
        _site.Polls.Enqueue("authorization_pending");

        var error = await Service().SignInAsync(_ => cancel.Cancel(), cancel.Token);

        Assert.Equal("CANCELLED", error!.Code);
        Assert.Empty(_site.Bodies("oauth/token"));
    }

    [Fact]
    public async Task An_unknown_client_says_sign_in_is_not_available()
    {
        _site.BeginStatus = 400;

        var error = await Service().SignInAsync(_ => { });

        Assert.Equal("INVALID_REQUEST", error!.Code);
        Assert.Contains("isn't available", error.Message, StringComparison.Ordinal);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_plain_http_code_page_is_refused()
    {
        _site.VerificationUri = "http://site.test/connect/";

        var error = await Service().SignInAsync(_ => { });

        Assert.Equal("BAD_RESPONSE", error!.Code);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_the_new_one_is_saved()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync(_ => { });

        Assert.Equal("acc-2", await account.GetAccessTokenAsync());
        Assert.Equal("ref-2", _store.Load()!.Value.RefreshToken);
        Assert.Equal("acc-3", await account.GetAccessTokenAsync());

        var refreshes = _site.Bodies("oauth/token").Select(FakeSite.Form).Where(f => f["grant_type"] == "refresh_token").Select(f => f["refresh_token"]);
        Assert.Equal(new[] { "ref-1", "ref-2" }, refreshes);
        Assert.Equal("ref-3", _store.Load()!.Value.RefreshToken);
    }

    [Fact]
    public async Task Restore_refreshes_the_session_and_picks_up_a_new_name_and_subscription()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Old name", Entitlements.None), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));

        var account = Service();
        await account.RestoreAsync();

        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.True(account.Entitlements.HasAccess);
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);
        Assert.True(_store.Load()!.Value.User.Entitlements.Has(Entitlements.Arena));
    }

    [Fact]
    public async Task Restore_keeps_an_offline_session_with_its_subscription_but_forgets_a_rejected_one()
    {
        var paid = new Entitlements(true, ["boosty"], [Entitlements.Arena], false, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        _store.Save("ref-gone", new AccountUser("u-42", "Anna", paid), expiresAt: null);

        _site.Offline = true;
        var offline = Service();
        await offline.RestoreAsync();
        Assert.True(offline.SignedIn);
        Assert.True(offline.Entitlements.Has(Entitlements.Arena));
        Assert.Equal(paid.CheckedAt, offline.Entitlements.CheckedAt);

        _site.Offline = false;
        var rejected = Service();
        await rejected.RestoreAsync();
        Assert.False(rejected.SignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public async Task The_sign_in_lasts_thirty_days_and_refreshing_does_not_extend_it()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync(_ => { });
        var end = account.SessionExpires!.Value;

        Assert.InRange(end - DateTime.UtcNow, TimeSpan.FromDays(30) - TimeSpan.FromMinutes(5), TimeSpan.FromDays(30));
        Assert.False(account.SessionEndingSoon);
        await account.GetAccessTokenAsync();
        Assert.Equal(end, account.SessionExpires);
        Assert.Equal(end, _store.Load()!.Value.ExpiresAt);
    }

    [Fact]
    public async Task A_restored_sign_in_near_its_end_asks_to_sign_in_again()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), DateTime.UtcNow.AddDays(1));

        var account = Service();
        await account.RestoreAsync();

        Assert.True(account.SessionEndingSoon);
        Assert.Equal(_store.Load()!.Value.ExpiresAt, account.SessionExpires);
    }

    [Fact]
    public async Task Signing_in_again_replaces_the_session_and_revokes_the_old_one()
    {
        var account = Service();
        await account.SignInAsync(_ => { });

        Assert.Null(await account.SignInAsync(_ => { }));

        Assert.Equal("ref-2", _store.Load()!.Value.RefreshToken);
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task A_refreshed_pair_is_saved_before_the_profile_is_read()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), expiresAt: null);
        _site.MeHold = new TaskCompletionSource();

        var account = Service();
        var restore = account.RestoreAsync();
        await _site.MeReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // /me is still in flight: had the app been closed now, the next start must present the new token.
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);
        _site.MeHold.SetResult();
        await restore;
        Assert.Equal("ref-1", _store.Load()!.Value.RefreshToken);
    }

    [Fact]
    public async Task Sign_out_revokes_the_refresh_token_and_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync(_ => { });

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Same(Entitlements.None, account.Entitlements);
        Assert.Null(_store.Load());
        Assert.Null(await account.GetAccessTokenAsync());
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task Sign_out_offline_still_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync(_ => { });
        _site.Offline = true;

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Me_parses_the_profile_and_paid_access()
    {
        using var doc = JsonDocument.Parse("""
            {"user": {"id": "u-7", "publicProfileId": "p", "profileUrl": "https://hearthpulse.net/profile/", "email": "a@b.c",
                      "name": " Zulut ", "avatarInitials": "ZU"},
             "subscription": {"hasAccess": true, "source": "boosty,patreon", "checkedAt": "2026-10-23T10:00:00.000Z", "stale": false,
                              "entitlements": {"arena": true, "battlegrounds": true, "standard": false, "contests": false,
                                               "guidesArchive": true, "arenaArticles": false, "battlegroundsArticles": false}}}
            """);

        var (user, profile) = AccountClient.ParseMe(doc.RootElement);

        Assert.Equal(("u-7", "Zulut"), (user.Id, user.DisplayName));
        Assert.Equal("https://hearthpulse.net/profile/", profile!.AbsoluteUri);
        var access = user.Entitlements;
        Assert.True(access.Boosty && access.Patreon && !access.Stale);
        Assert.True(access.Has(Entitlements.Arena) && access.Has(Entitlements.GuidesArchive));
        Assert.False(access.Has(Entitlements.Standard));
        Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc), access.CheckedAt);
        Assert.Equal("Boosty + Patreon subscription: Arena, Battlegrounds, Guides archive", access.Summary);
    }

    [Theory]
    [InlineData("""{"hasAccess": false, "source": "none", "checkedAt": null, "stale": true, "entitlements": {"arena": false}}""")]
    [InlineData("""{"hasAccess": true, "source": "boosty", "entitlements": {}}""")]
    [InlineData("""{"hasAccess": "yes", "entitlements": {"arena": "true"}}""")]
    [InlineData("null")]
    public void Missing_or_unconfirmed_subscription_means_no_paid_access(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var access = Entitlements.Parse(doc.RootElement);

        Assert.False(access.HasAccess);
        Assert.False(access.Has(Entitlements.Arena));
        Assert.Equal("No paid subscription", access.Summary);
    }

    [Fact]
    public void Every_section_reads_as_all_sections()
    {
        using var doc = JsonDocument.Parse("""
            {"hasAccess": true, "source": "telegram", "stale": false, "entitlements": {"arena": true, "battlegrounds": true, "standard": true,
             "contests": true, "guidesArchive": true, "arenaArticles": true, "battlegroundsArticles": true}}
            """);

        Assert.Equal("Telegram subscription: all sections", Entitlements.Parse(doc.RootElement).Summary);
    }

    [Theory]
    [InlineData(400, """{"error":"slow_down"}""", "SLOW_DOWN")]
    [InlineData(400, """{"error":"authorization_pending"}""", "AUTHORIZATION_PENDING")]
    [InlineData(401, """{"error":{"code":"INVALID_ACCESS_TOKEN","message":"Access token is invalid or expired"}}""", "INVALID_ACCESS_TOKEN")]
    [InlineData(403, """{"error":{"code":"INSUFFICIENT_SCOPE","message":"x"}}""", "INSUFFICIENT_SCOPE")]
    [InlineData(429, """{"error":"temporarily_unavailable"}""", "TEMPORARILY_UNAVAILABLE")]
    [InlineData(404, "", "NOT_AVAILABLE")]
    [InlineData(502, "<html>bad gateway</html>", "SERVER_ERROR")]
    public void Errors_parse_from_both_envelopes(int status, string body, string code) =>
        Assert.Equal(code, Assert.Throws<AccountException>(() => AccountClient.Parse(status, body)).Code);

    /// <summary>The site's application API in miniature, answering by path under <see cref="Base"/>.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        public const string Base = "https://site.test/api/v1/";
        private readonly List<(string Path, string Body)> _requests = [];
        private readonly HashSet<string> _refreshTokens = [];
        private readonly HashSet<string> _accessTokens = [];
        private int _issued;

        /// <summary>Answers to the next device-code polls; once empty the player has confirmed. "offline" drops the request.</summary>
        public Queue<string> Polls { get; } = new();

        public bool Offline { get; set; }

        /// <summary>When set, <c>/me</c> waits for it (after signalling <see cref="MeReached"/>).</summary>
        public TaskCompletionSource? MeHold { get; set; }

        public TaskCompletionSource MeReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BeginStatus { get; set; } = 200;

        public string VerificationUri { get; set; } = "https://site.test/connect/";

        public int ExpiresIn { get; set; } = 900;

        public IEnumerable<string> Bodies(string path)
        {
            lock (_requests)
            {
                return _requests.Where(r => r.Path == path).Select(r => r.Body).ToList();
            }
        }

        /// <summary>Makes <paramref name="refreshToken"/> one the site accepts (a session from an earlier start).</summary>
        public void Issue(string refreshToken) => _refreshTokens.Add(refreshToken);

        public static Dictionary<string, string> Form(string body) =>
            body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Decode(p[0]), p => p.Length > 1 ? Decode(p[1]) : string.Empty);

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            if (MeHold is not null && path == "me")
            {
                MeReached.TrySetResult();
                await MeHold.Task;
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add((path, body));
            }

            var form = Form(body);
            return path switch
            {
                "oauth/device/code" when BeginStatus != 200 => Json(BeginStatus, new { error = "invalid_request" }),
                "oauth/device/code" => Json(200, new
                {
                    device_code = "mca_device_1",
                    user_code = "ABCD-EFGH",
                    verification_uri = VerificationUri,
                    verification_uri_complete = VerificationUri + "?user_code=ABCD-EFGH",
                    expires_in = 600,
                    interval = 5,
                }),
                "oauth/token" => Token(form),
                "oauth/revoke" => Revoke(form),
                "me" when request.Headers.Authorization?.Parameter is { } bearer && _accessTokens.Contains(bearer) => Json(200, new
                {
                    user = new { id = "u-42", publicProfileId = "", profileUrl = "https://site.test/profile/", email = "a@b.c", name = "Anna", avatarInitials = "AN" },
                    subscription = new
                    {
                        hasAccess = true, source = "boosty", checkedAt = "2026-10-23T10:00:00.000Z", stale = false,
                        entitlements = new { arena = true, battlegrounds = true, standard = false, contests = false, guidesArchive = false, arenaArticles = false, battlegroundsArticles = false },
                    },
                }),
                "me" => Json(401, new { error = new { code = "INVALID_ACCESS_TOKEN", message = "Access token is invalid or expired" } }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Token(Dictionary<string, string> form)
        {
            if (form.GetValueOrDefault("client_id") != AccountClient.ClientId)
            {
                return Json(400, new { error = "invalid_grant" });
            }

            switch (form.GetValueOrDefault("grant_type"))
            {
                case AccountClient.DeviceGrant when form.GetValueOrDefault("device_code") == "mca_device_1":
                    if (Polls.TryDequeue(out var answer))
                    {
                        return answer == "offline" ? throw new HttpRequestException("offline") : Json(400, new { error = answer });
                    }

                    break;
                case "refresh_token" when _refreshTokens.Remove(form.GetValueOrDefault("refresh_token") ?? string.Empty):
                    break;
                default:
                    return Json(400, new { error = "invalid_grant" });
            }

            var n = ++_issued;
            _refreshTokens.Add($"ref-{n}");
            _accessTokens.Add($"acc-{n}");
            return Json(200, new { access_token = $"acc-{n}", refresh_token = $"ref-{n}", token_type = "Bearer", expires_in = ExpiresIn, scope = AccountClient.Scope });
        }

        private HttpResponseMessage Revoke(Dictionary<string, string> form)
        {
            _refreshTokens.Remove(form.GetValueOrDefault("token") ?? string.Empty);
            return Json(200, new { });
        }

        private static HttpResponseMessage Json(int status, object value) =>
            new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
