using System.Globalization;
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

    private const string Site = "https://site.test";

    private DateTime _now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private AccountClient Client() => new(new HttpClient(_site), FakeSite.Base);

    private AccountService Service(bool browserOpens = true) => new(Client(), _store, url => { _opened.Add(url); return browserOpens; })
    {
        Delay = (wait, token) =>
        {
            _delays.Add(wait);
            _now += wait;
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        },
        UtcNow = () => _now,
    };

    private static double[] Seconds(IEnumerable<TimeSpan> waits) => waits.Select(w => w.TotalSeconds).ToArray();

    [Fact]
    public async Task Device_flow_shows_the_code_waits_for_confirmation_and_reads_the_profile()
    {
        _site.Polls.Enqueue("authorization_pending");
        _site.Polls.Enqueue("authorization_pending");
        var account = Service();
        DeviceCode? shown = null;

        Assert.Null(await account.SignInAsync((code, _) => shown = code));

        Assert.Equal("ABCD-EFGH", shown!.UserCode);
        Assert.Equal("site.test/connect", shown.DisplayUri);
        Assert.Equal("https://site.test/connect/?user_code=ABCD-EFGH", Assert.Single(_opened).AbsoluteUri);
        Assert.Equal(new[] { 5.0, 5, 5 }, Seconds(_delays));
        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.Equal("u-42", account.User.Id);
        Assert.True(account.Entitlements.Has(Entitlements.Battlegrounds));
        Assert.Equal("https://site.test/profile/", account.ProfileUrl.AbsoluteUri);
        Assert.Equal("acc-1", await account.GetAccessTokenAsync());
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);

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

        Assert.Null(await Service().SignInAsync((_, _) => { }));

        Assert.Equal(new[] { 5.0, 10, 10, 15 }, Seconds(_delays));
    }

    [Fact]
    public async Task A_short_outage_while_waiting_is_ridden_out()
    {
        _site.Polls.Enqueue("offline");
        _site.Polls.Enqueue("authorization_pending");

        var account = Service();

        Assert.Null(await account.SignInAsync((_, _) => { }));
        Assert.True(account.SignedIn);
        // The outage backs off on top of the interval and resets once the site answers; only slow_down grows it for good.
        Assert.Equal(new[] { 5.0, 10, 5 }, Seconds(_delays));
    }

    [Fact]
    public async Task A_long_outage_backs_off_at_most_thirty_seconds()
    {
        for (var i = 0; i < 10; i++)
        {
            _site.Polls.Enqueue("offline");
        }

        Assert.Null(await Service().SignInAsync((_, _) => { }));

        Assert.Equal(35, _delays.Max().TotalSeconds);
        Assert.Equal(35, _delays[^1].TotalSeconds);
    }

    [Fact]
    public async Task The_code_runs_out_locally_too()
    {
        _site.AlwaysPending = true;

        var error = await Service().SignInAsync((_, _) => { });

        Assert.Equal("EXPIRED_TOKEN", error!.Code);
        Assert.InRange(_delays.Count, 119, 122);
    }

    [Fact]
    public async Task The_card_learns_whether_the_browser_opened()
    {
        var opened = new List<bool>();

        await Service(browserOpens: false).SignInAsync((_, ok) => opened.Add(ok));

        Assert.Equal(new[] { false }, opened);
    }

    [Fact]
    public async Task Tokens_from_the_last_poll_are_kept_even_after_cancel()
    {
        using var cancel = new CancellationTokenSource();
        _site.TokenHold = new TaskCompletionSource();
        var account = Service();

        var signIn = account.SignInAsync((_, _) => { }, cancel.Token);
        await _site.TokenReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        _site.TokenHold.SetResult();

        Assert.Null(await signIn);
        Assert.True(account.SignedIn);
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
    }

    [Fact]
    public async Task A_failed_profile_after_the_poll_hands_the_tokens_back()
    {
        _site.MeStatus = 503;
        var account = Service();

        var error = await account.SignInAsync((_, _) => { });

        Assert.Equal("SERVER_ERROR", error!.Code);
        Assert.False(account.SignedIn);
        Assert.Null(_store.Load(Site));
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task A_failed_second_sign_in_keeps_the_first_session()
    {
        var account = Service();
        await account.SignInAsync((_, _) => { });
        _site.MeStatus = 503;

        Assert.NotNull(await account.SignInAsync((_, _) => { }));

        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
        Assert.Equal("ref-2", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
        _site.MeStatus = null;
        Assert.Equal("acc-1", await account.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_refresh_finishes_and_is_saved_even_if_the_caller_stops_waiting()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync((_, _) => { });
        _site.ExpiresIn = 900;
        _site.TokenHold = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();

        var call = account.GetAccessTokenAsync(cancel.Token);
        await _site.TokenReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        _site.TokenHold.SetResult();

        // The site rotated ref-1; the app must now hold ref-2, or the next refresh would revoke the whole sign-in.
        Assert.Equal("acc-2", await account.GetAccessTokenAsync());
        Assert.Equal("ref-2", _store.Load(Site)!.Value.RefreshToken);
        Assert.True(account.SignedIn);
    }

    [Fact]
    public async Task A_rotated_token_presented_again_ends_the_sign_in_and_the_app_forgets_it()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync((_, _) => { });
        _site.Rotate("ref-1");

        Assert.Null(await account.GetAccessTokenAsync());

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load(Site));
    }

    [Fact]
    public async Task Parallel_callers_share_one_refresh()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync((_, _) => { });
        _site.ExpiresIn = 900;

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => account.GetAccessTokenAsync()));

        Assert.All(tokens, t => Assert.Equal("acc-2", t));
        Assert.Single(_site.Bodies("oauth/token").Select(FakeSite.Form), f => f["grant_type"] == "refresh_token");
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

        var error = await account.SignInAsync((_, _) => { });

        Assert.Equal(code, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(account.SignedIn);
        Assert.Null(_store.Load(Site));
        Assert.Empty(_site.Bodies("me"));
    }

    [Fact]
    public async Task Cancel_stops_waiting()
    {
        using var cancel = new CancellationTokenSource();
        _site.Polls.Enqueue("authorization_pending");

        var error = await Service().SignInAsync((_, _) => cancel.Cancel(), cancel.Token);

        Assert.Equal("CANCELLED", error!.Code);
        Assert.Empty(_site.Bodies("oauth/token"));
    }

    [Fact]
    public async Task An_unknown_client_says_sign_in_is_not_available()
    {
        _site.BeginStatus = 400;

        var error = await Service().SignInAsync((_, _) => { });

        Assert.Equal("INVALID_REQUEST", error!.Code);
        Assert.Contains("isn't available", error.Message, StringComparison.Ordinal);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_plain_http_code_page_is_refused()
    {
        _site.VerificationUri = "http://site.test/connect/";

        var error = await Service().SignInAsync((_, _) => { });

        Assert.Equal("BAD_RESPONSE", error!.Code);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_the_new_one_is_saved()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync((_, _) => { });

        Assert.Equal("acc-2", await account.GetAccessTokenAsync());
        Assert.Equal("ref-2", _store.Load(Site)!.Value.RefreshToken);
        Assert.Equal("acc-3", await account.GetAccessTokenAsync());

        var refreshes = _site.Bodies("oauth/token").Select(FakeSite.Form).Where(f => f["grant_type"] == "refresh_token").Select(f => f["refresh_token"]);
        Assert.Equal(new[] { "ref-1", "ref-2" }, refreshes);
        Assert.Equal("ref-3", _store.Load(Site)!.Value.RefreshToken);
    }

    [Fact]
    public async Task Restore_refreshes_the_session_and_picks_up_a_new_name_and_subscription()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Old name", Entitlements.None), new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), Site);

        var account = Service();
        await account.RestoreAsync();

        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.True(account.Entitlements.HasAccess);
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
        Assert.True(_store.Load(Site)!.Value.User.Entitlements.Has(Entitlements.Arena));
    }

    [Fact]
    public async Task Restore_keeps_an_offline_session_with_its_subscription_but_forgets_a_rejected_one()
    {
        var paid = new Entitlements(true, ["boosty"], [Entitlements.Arena], false, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        _store.Save("ref-gone", new AccountUser("u-42", "Anna", paid), expiresAt: null, Site);

        _site.Offline = true;
        var offline = Service();
        await offline.RestoreAsync();
        Assert.True(offline.SignedIn);
        Assert.True(offline.Entitlements.Has(Entitlements.Arena));
        Assert.Equal(paid.CheckedAt, offline.Entitlements.CheckedAt);
        // Only what the site said last time: a strict paywall can tell.
        Assert.True(offline.Entitlements.Stale);

        _site.Offline = false;
        var rejected = Service();
        await rejected.RestoreAsync();
        Assert.False(rejected.SignedIn);
        Assert.Null(_store.Load(Site));
    }

    [Fact]
    public async Task The_sign_in_lasts_thirty_days_and_refreshing_does_not_extend_it()
    {
        _site.ExpiresIn = 1;
        var account = Service();
        await account.SignInAsync((_, _) => { });
        var end = account.SessionExpires!.Value;

        Assert.InRange(end - _now, TimeSpan.FromDays(30) - TimeSpan.FromMinutes(5), TimeSpan.FromDays(30));
        Assert.False(account.SessionEndingSoon);
        await account.GetAccessTokenAsync();
        Assert.Equal(end, account.SessionExpires);
        Assert.Equal(end, _store.Load(Site)!.Value.ExpiresAt);
    }

    [Fact]
    public async Task A_restored_sign_in_near_its_end_asks_to_sign_in_again()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), DateTime.UtcNow.AddDays(1), Site);

        var account = Service();
        await account.RestoreAsync();

        Assert.True(account.SessionEndingSoon);
        Assert.Equal(_store.Load(Site)!.Value.ExpiresAt, account.SessionExpires);
    }

    [Fact]
    public async Task Signing_in_again_replaces_the_session_and_revokes_the_old_one()
    {
        var account = Service();
        await account.SignInAsync((_, _) => { });

        Assert.Null(await account.SignInAsync((_, _) => { }));

        Assert.Equal("ref-2", _store.Load(Site)!.Value.RefreshToken);
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task A_refreshed_pair_is_saved_before_the_profile_is_read()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), expiresAt: null, Site);
        _site.MeHold = new TaskCompletionSource();

        var account = Service();
        var restore = account.RestoreAsync();
        await _site.MeReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // /me is still in flight: had the app been closed now, the next start must present the new token.
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
        _site.MeHold.SetResult();
        await restore;
        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
    }

    [Fact]
    public async Task Sign_out_revokes_the_refresh_token_and_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync((_, _) => { });

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Same(Entitlements.None, account.Entitlements);
        Assert.Null(_store.Load(Site));
        Assert.Null(await account.GetAccessTokenAsync());
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task Sign_out_offline_still_forgets_the_session()
    {
        var account = Service();
        await account.SignInAsync((_, _) => { });
        _site.Offline = true;

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load(Site));
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

    [Fact]
    public async Task A_rejected_profile_after_refresh_revokes_the_new_pair()
    {
        _site.Issue("ref-saved");
        _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), expiresAt: null, Site);
        _site.MeStatus = 403;

        var account = Service();
        await account.RestoreAsync();

        Assert.False(account.SignedIn);
        Assert.Null(_store.Load(Site));
        Assert.Equal("ref-1", FakeSite.Form(_site.Bodies("oauth/revoke").Single())["token"]);
    }

    [Fact]
    public async Task The_end_of_the_sign_in_is_noticed_while_the_app_runs()
    {
        var account = Service();
        await account.SignInAsync((_, _) => { });
        var changes = 0;
        account.Changed += () => changes++;

        _now += TimeSpan.FromDays(20);
        account.CheckSessionPhase();
        Assert.Equal(0, changes);

        _now += TimeSpan.FromDays(9);
        account.CheckSessionPhase();
        Assert.Equal(1, changes);
        Assert.True(account.SessionEndingSoon);

        _now += TimeSpan.FromDays(2);
        account.CheckSessionPhase();
        Assert.Equal(2, changes);
        Assert.StartsWith("Sign-in ended on", account.SessionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_note_uses_english_month_names_under_any_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            _site.Issue("ref-saved");
            _store.Save("ref-saved", new AccountUser("u-42", "Anna", Entitlements.None), new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc), Site);
            var account = Service();
            await account.RestoreAsync();

            Assert.Equal("Signed in until 22 Nov 2026", account.SessionNote);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_session_saved_for_another_site_or_by_an_older_version_is_dropped()
    {
        _store.Save("ref-x", new AccountUser("u-42", "Anna", Entitlements.None), expiresAt: null, "https://other.test");
        Assert.Null(_store.Load(Site));
        Assert.False(File.Exists(Path.Combine(_dir, "account.dat")));

        // The OIDC draft's file: no Format, no Site.
        File.WriteAllBytes(Path.Combine(_dir, "account.dat"), Protect("""{"RefreshToken":"old","Id":"u","DisplayName":"Anna"}"""));
        Assert.Null(_store.Load(Site));
    }

    [Fact]
    public void A_failed_write_leaves_the_last_good_file()
    {
        var user = new AccountUser("u-42", "Anna", Entitlements.None);
        Assert.True(_store.Save("ref-1", user, expiresAt: null, Site));

        using (new FileStream(Path.Combine(_dir, "account.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_store.Save("ref-2", user, expiresAt: null, Site));
        }

        Assert.Equal("ref-1", _store.Load(Site)!.Value.RefreshToken);
        Assert.False(File.Exists(Path.Combine(_dir, "account.dat.tmp")));
    }

    [Theory]
    [InlineData("http://site.test/api/v1", "https://hearthpulse.net/api/v1/")]
    [InlineData("not a url", "https://hearthpulse.net/api/v1/")]
    [InlineData("https://staging.site.test/api/v1", "https://staging.site.test/api/v1/")]
    [InlineData("http://127.0.0.1:8080/api/v1/", "http://127.0.0.1:8080/api/v1/")]
    public void Only_an_https_site_gets_the_tokens(string configured, string used) =>
        Assert.Equal(used, new AccountClient(new HttpClient(), configured).BaseUri.AbsoluteUri);

    [Fact]
    public async Task A_code_page_on_another_host_is_refused()
    {
        _site.VerificationUri = "https://evil.test/connect/";

        var error = await Service().SignInAsync((_, _) => { });

        Assert.Equal("BAD_RESPONSE", error!.Code);
        Assert.Empty(_opened);
    }

    private static byte[] Protect(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return OperatingSystem.IsWindows() ? TokenStore.Dpapi.Protect(bytes) : bytes;
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
        private readonly HashSet<string> _rotated = [];
        private int _issued;

        /// <summary>The player never confirms.</summary>
        public bool AlwaysPending { get; set; }

        /// <summary>When set, <c>/me</c> answers this status with an API error.</summary>
        public int? MeStatus { get; set; }

        /// <summary>When set, a token request that issues tokens waits for it after the site has rotated (see <see cref="TokenReached"/>).</summary>
        public TaskCompletionSource? TokenHold { get; set; }

        public TaskCompletionSource TokenReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        /// <summary>Marks <paramref name="refreshToken"/> as already rotated, as if another refresh had used it.</summary>
        public void Rotate(string refreshToken)
        {
            _refreshTokens.Remove(refreshToken);
            _rotated.Add(refreshToken);
        }

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
            if (path == "oauth/token")
            {
                var answer = Token(form);
                if (TokenHold is not null && answer.IsSuccessStatusCode)
                {
                    TokenReached.TrySetResult();
                    await TokenHold.Task;
                }

                return answer;
            }

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
                "oauth/revoke" => Revoke(form),
                "me" when MeStatus is { } status => Json(status, new { error = new { code = status == 403 ? "INSUFFICIENT_SCOPE" : "SERVER_ERROR", message = "x" } }),
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
                    if (AlwaysPending)
                    {
                        return Json(400, new { error = "authorization_pending" });
                    }

                    if (Polls.TryDequeue(out var answer))
                    {
                        return answer == "offline" ? throw new HttpRequestException("offline") : Json(400, new { error = answer });
                    }

                    break;
                case "refresh_token" when _rotated.Contains(form.GetValueOrDefault("refresh_token") ?? string.Empty):
                    // Like the site: a rotated token coming back revokes the whole family.
                    _refreshTokens.Clear();
                    _accessTokens.Clear();
                    return Json(400, new { error = "invalid_grant" });
                case "refresh_token" when _refreshTokens.Remove(form.GetValueOrDefault("refresh_token") ?? string.Empty):
                    _rotated.Add(form["refresh_token"]);
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
