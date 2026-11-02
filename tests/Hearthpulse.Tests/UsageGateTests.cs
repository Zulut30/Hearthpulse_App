using System.Globalization;
using System.Net;
using System.Text;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>Daily free use of the stats pool's numbers (plan 3.1): the site's limits, its claims and the soft mode here.</summary>
public sealed class UsageGateTests : IDisposable
{
    private static readonly DateTime Midnight = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-usage-{Guid.NewGuid():N}");
    private readonly FakeUsageSite _site = new();
    private DateTime _now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    public UsageGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AccountClient Client() => new(new HttpClient(_site), FakeUsageSite.Base);

    private UsageGate Gate(AccountService? account = null, TimeSpan? timeout = null) =>
        new(Client(), account, Path.Combine(_dir, "usage.json")) { UtcNow = () => _now, ClaimTimeout = timeout ?? TimeSpan.FromSeconds(5) };

    /// <summary>The last game ended (the hosts call this on the session's GameFinished), then the next one is asked for.</summary>
    private static Task<bool> NextAsync(UsageGate gate, object game)
    {
        gate.GameFinished();
        return UseAsync(gate, game);
    }

    /// <summary>Asks for the game and waits for the decision, as the next snapshot would.</summary>
    private static async Task<bool> UseAsync(UsageGate gate, object game)
    {
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () => decided.TrySetResult();
        if (gate.Allow(UsageGate.BgPoolStats, game))
        {
            return true;
        }

        if (gate.Decision(UsageGate.BgPoolStats) is null)
        {
            await decided.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        return gate.Allow(UsageGate.BgPoolStats, game);
    }

    private static string Left(int left) => Loc.F("Usage.Limit.Left", left, 3);

    private static string Reached() => Loc.F("Usage.Limit.Reached", 3, UsageGate.ResetText(Midnight));

    [Fact]
    public async Task Nothing_is_limited_until_the_site_says_limits_are_on()
    {
        var gate = Gate();
        Assert.True(gate.Allow(UsageGate.BgPoolStats, 1));

        _site.Enabled = false;
        await gate.RefreshLimitsAsync();
        for (var game = 0; game < 10; game++)
        {
            Assert.True(gate.Allow(UsageGate.BgPoolStats, game));
        }

        Assert.Empty(_site.Claims);
        Assert.Equal(string.Empty, gate.Note(UsageGate.BgPoolStats));
    }

    [Fact]
    public async Task Signed_out_three_games_a_day_are_counted_here_with_what_is_left_and_then_why()
    {
        var gate = Gate();
        await gate.RefreshLimitsAsync();

        for (var game = 1; game <= 3; game++)
        {
            Assert.True(await NextAsync(gate, game));
            Assert.Equal(Left(3 - game), gate.Note(UsageGate.BgPoolStats));
        }

        Assert.True(await UseAsync(gate, 3), "the same game again");
        Assert.False(await NextAsync(gate, 4));
        Assert.Equal(Reached(), gate.Note(UsageGate.BgPoolStats));
        Assert.Empty(_site.Claims);

        // The count is kept on disk: a restart does not reset it. The next UTC day does.
        var restarted = Gate();
        Assert.False(await NextAsync(restarted, 5));
        _now = _now.AddDays(1);
        Assert.True(await NextAsync(restarted, 6));
    }

    [Fact]
    public async Task Signed_in_the_site_counts_and_decides_while_the_numbers_wait()
    {
        var account = await SignedInAsync(subscription: false);
        var gate = Gate(account);
        await gate.RefreshLimitsAsync();

        _site.Hold = new TaskCompletionSource();
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () => decided.TrySetResult();
        Assert.False(gate.Allow(UsageGate.BgPoolStats, "game-1"), "waiting for the site");
        Assert.Equal(string.Empty, gate.Note(UsageGate.BgPoolStats));
        _site.Hold.SetResult();
        await decided.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(gate.Allow(UsageGate.BgPoolStats, "game-1"));
        var claim = Assert.Single(_site.Claims);
        Assert.Equal(UsageGate.BgPoolStats, claim.Feature);
        Assert.True(Guid.TryParse(claim.GameId, out _), "a UUID, not a time");

        _site.Allowed = false;
        Assert.False(await NextAsync(gate, "game-2"));
        Assert.Equal(Reached(), gate.Note(UsageGate.BgPoolStats));
    }

    [Fact]
    public async Task A_restart_in_the_same_game_does_not_count_again()
    {
        var account = await SignedInAsync(subscription: false);
        var start = new DateTime(2026, 11, 2, 11, 50, 0, DateTimeKind.Utc);
        Assert.True(await UseAsync(await RefreshedAsync(Gate(account)), start));

        // A new gate (the app started again) reads the same game from the log: same id, decided already.
        var restarted = await RefreshedAsync(Gate(account));
        Assert.True(restarted.Allow(UsageGate.BgPoolStats, start));
        Assert.Single(_site.Claims);
    }

    [Fact]
    public async Task A_reconnect_keeps_the_game_s_decision_and_a_finished_game_does_not()
    {
        var account = await SignedInAsync(subscription: false);
        var gate = await RefreshedAsync(Gate(account));
        Assert.True(await UseAsync(gate, new DateTime(2026, 11, 2, 11, 0, 0, DateTimeKind.Utc)));

        // Reconnecting starts a new game in the log; the site is not asked again even at 3/3.
        _site.Allowed = false;
        Assert.True(gate.Allow(UsageGate.BgPoolStats, new DateTime(2026, 11, 2, 11, 20, 0, DateTimeKind.Utc)));
        Assert.Single(_site.Claims);

        Assert.False(await NextAsync(gate, new DateTime(2026, 11, 2, 11, 40, 0, DateTimeKind.Utc)));
        Assert.Equal(2, _site.Claims.Count);
    }

    [Fact]
    public async Task A_subscriber_is_never_limited_and_never_counted()
    {
        var account = await SignedInAsync(subscription: true);
        var gate = await RefreshedAsync(Gate(account));

        for (var game = 0; game < 10; game++)
        {
            Assert.True(gate.Allow(UsageGate.BgPoolStats, game));
        }

        Assert.Empty(_site.Claims);
        Assert.Equal(string.Empty, gate.Note(UsageGate.BgPoolStats));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("timeout")]
    [InlineData("500")]
    public async Task Any_failure_of_the_site_counts_the_game_here_soft_mode(string failure)
    {
        var account = await SignedInAsync(subscription: false);
        var gate = await RefreshedAsync(Gate(account, TimeSpan.FromMilliseconds(200)));
        switch (failure)
        {
            case "offline":
                _site.Offline = true;
                break;
            case "timeout":
                _site.Hold = new TaskCompletionSource();
                break;
            default:
                _site.ClaimStatus = 500;
                break;
        }

        Assert.True(await NextAsync(gate, 1));
        Assert.True(await NextAsync(gate, 2));
        Assert.True(await NextAsync(gate, 3));
        Assert.False(await NextAsync(gate, 4));
        _site.Hold?.TrySetResult();
    }

    [Fact]
    public void The_reset_time_is_the_player_s_clock()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // ru-RU writes the short time as "3:00" (Windows) or "03:00" (ICU on Linux and macOS): the local hour of the UTC
            // midnight, without a date.
            var ru = new CultureInfo("ru-RU");
            CultureInfo.CurrentCulture = ru;
            Assert.Matches(@"^\d{1,2}:\d{2}$", UsageGate.ResetText(Midnight));
            Assert.Equal(Midnight.ToLocalTime().ToString("t", ru), UsageGate.ResetText(Midnight));
            Assert.Equal("00:00", UsageGate.ResetText(DateTime.MinValue));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Limits_and_claims_parse_only_the_site_s_shape()
    {
        Assert.Null(UsageLimits.Parse("""{"enabled":"yes","features":{}}"""));
        var limits = UsageLimits.Parse("""{"enabled":true,"resetsAt":"2026-11-03T00:00:00.000Z","features":{"bg.poolStats":{"section":"battlegrounds","freePerDay":3}}}""");
        Assert.Equal(new UsageFeature("battlegrounds", 3), limits?.Features[UsageGate.BgPoolStats]);
        Assert.Null(UsageClaim.Parse("not json"));
        Assert.Equal(new UsageClaim(false, false, 3, 3, Midnight),
            UsageClaim.Parse("""{"allowed":false,"unlimited":false,"used":3,"limit":3,"resetsAt":"2026-11-03T00:00:00.000Z"}"""));
    }

    private static async Task<UsageGate> RefreshedAsync(UsageGate gate)
    {
        await gate.RefreshLimitsAsync();
        return gate;
    }

    private async Task<AccountService> SignedInAsync(bool subscription)
    {
        _site.Subscriber = subscription;
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        var client = Client();
        Assert.True(store.Save("ref-1", new AccountUser("u-1", "Player", Entitlements.None), DateTime.UtcNow.AddDays(30), client.Site));
        var account = new AccountService(client, store);
        await account.RestoreAsync();
        Assert.True(account.SignedIn);
        Assert.Equal(subscription, account.Entitlements.Has(Entitlements.Battlegrounds));
        return account;
    }

    /// <summary>The site's token, profile and usage endpoints in miniature; it counts like the site (a game once).</summary>
    private sealed class FakeUsageSite : HttpMessageHandler
    {
        public const string Base = "https://site.test/api/v1/";

        public bool Enabled { get; set; } = true;

        public bool Allowed { get; set; } = true;

        public bool Offline { get; set; }

        public bool Subscriber { get; set; }

        public int ClaimStatus { get; set; } = 200;

        /// <summary>When set, a claim waits for it (or for the app to give up).</summary>
        public TaskCompletionSource? Hold { get; set; }

        public List<(string Feature, string GameId)> Claims { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            if (Offline && path.StartsWith("usage/claim", StringComparison.Ordinal))
            {
                throw new HttpRequestException("offline");
            }

            switch (path)
            {
                case "oauth/token":
                    return Json(200, """{"access_token":"acc","token_type":"Bearer","refresh_token":"ref-2","expires_in":900}""");
                case "me":
                    return Json(200, Subscriber
                        ? """{"user":{"id":"u-1","name":"Player"},"subscription":{"hasAccess":true,"source":"boosty","entitlements":{"battlegrounds":true}}}"""
                        : """{"user":{"id":"u-1","name":"Player"}}""");
                case "usage/limits":
                    return Json(200, """{"enabled":ENABLED,"day":"2026-11-02","resetsAt":"2026-11-03T00:00:00.000Z","features":{"bg.poolStats":{"section":"battlegrounds","freePerDay":3}}}"""
                        .Replace("ENABLED", Enabled ? "true" : "false", StringComparison.Ordinal));
                case "usage/claim":
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    {
                        Claims.Add((doc.RootElement.GetProperty("feature").GetString()!, doc.RootElement.GetProperty("gameId").GetString()!));
                    }

                    if (Hold is not null)
                    {
                        await Hold.Task.WaitAsync(cancellationToken);
                    }

                    return ClaimStatus != 200
                        ? Json(ClaimStatus, """{"error":{"code":"X","message":"x"}}""")
                        : Json(200, """{"feature":"bg.poolStats","allowed":ALLOWED,"unlimited":false,"used":USED,"limit":3,"resetsAt":"2026-11-03T00:00:00.000Z"}"""
                            .Replace("ALLOWED", Allowed ? "true" : "false", StringComparison.Ordinal)
                            .Replace("USED", Math.Min(3, Claims.Select(c => c.GameId).Distinct().Count()).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
