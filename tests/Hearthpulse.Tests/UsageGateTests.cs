using System.Net;
using System.Text;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>Daily free use of the stats pool's numbers (plan 3.1): the site's limits, its claims and the soft mode here.</summary>
public sealed class UsageGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-usage-{Guid.NewGuid():N}");
    private readonly FakeUsageSite _site = new();
    private DateTime _now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    public UsageGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AccountClient Client() => new(new HttpClient(_site), FakeUsageSite.Base);

    private UsageGate Gate(AccountService? account = null) =>
        new(Client(), account, Path.Combine(_dir, "usage.json")) { UtcNow = () => _now };

    /// <summary>Asks for the game and waits for the decision, as the next snapshot would.</summary>
    private static async Task<bool> UseAsync(UsageGate gate, object game)
    {
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Changed += () => decided.TrySetResult();
        if (gate.Allow(UsageGate.BgPoolStats, game))
        {
            return true;
        }

        await decided.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return gate.Allow(UsageGate.BgPoolStats, game);
    }

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
    }

    [Fact]
    public async Task Signed_out_three_games_a_day_are_counted_here_and_the_fourth_explains_why()
    {
        var gate = Gate();
        await gate.RefreshLimitsAsync();

        for (var game = 1; game <= 3; game++)
        {
            Assert.True(await UseAsync(gate, game));
        }

        Assert.True(await UseAsync(gate, 3), "the same game again");
        Assert.False(await UseAsync(gate, 4));
        Assert.Equal(Note(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)), gate.Note(UsageGate.BgPoolStats));
        Assert.Empty(_site.Claims);

        // The count is kept on disk: a restart does not reset it. The next UTC day does.
        var restarted = Gate();
        Assert.False(await UseAsync(restarted, 5));
        _now = _now.AddDays(1);
        Assert.True(await UseAsync(restarted, 6));
        Assert.Equal(string.Empty, restarted.Note(UsageGate.BgPoolStats));
    }

    [Fact]
    public async Task Signed_in_the_site_counts_and_decides()
    {
        var account = await SignedInAsync(subscription: false);
        var gate = Gate(account);
        await gate.RefreshLimitsAsync();

        _site.Allowed = false;
        Assert.False(await UseAsync(gate, "game-1"));
        var claim = Assert.Single(_site.Claims);
        Assert.Equal(UsageGate.BgPoolStats, claim.Feature);
        Assert.True(Guid.TryParse(claim.GameId, out _), "a random id per game, not a time");
        Assert.Equal(Note(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)), gate.Note(UsageGate.BgPoolStats));

        _site.Allowed = true;
        Assert.True(await UseAsync(gate, "game-2"));
        Assert.Equal(2, _site.Claims.Count);
    }

    [Fact]
    public async Task A_subscriber_is_never_limited_and_never_counted()
    {
        var account = await SignedInAsync(subscription: true);
        var gate = Gate(account);
        await gate.RefreshLimitsAsync();

        for (var game = 0; game < 10; game++)
        {
            Assert.True(gate.Allow(UsageGate.BgPoolStats, game));
        }

        Assert.Empty(_site.Claims);
    }

    [Fact]
    public async Task Offline_the_game_is_counted_here_soft_mode()
    {
        var account = await SignedInAsync(subscription: false);
        var gate = Gate(account);
        await gate.RefreshLimitsAsync();
        _site.Offline = true;

        Assert.True(await UseAsync(gate, 1));
        Assert.True(await UseAsync(gate, 2));
        Assert.True(await UseAsync(gate, 3));
        Assert.False(await UseAsync(gate, 4));
    }

    [Fact]
    public void Limits_and_claims_parse_only_the_site_s_shape()
    {
        Assert.Null(UsageLimits.Parse("""{"enabled":"yes","features":{}}"""));
        var limits = UsageLimits.Parse("""{"enabled":true,"resetsAt":"2026-11-03T00:00:00.000Z","features":{"bg.poolStats":{"section":"battlegrounds","freePerDay":3}}}""");
        Assert.Equal(new UsageFeature("battlegrounds", 3), limits?.Features[UsageGate.BgPoolStats]);
        Assert.Null(UsageClaim.Parse("not json"));
        Assert.Equal(new UsageClaim(false, false, 3, 3, new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)),
            UsageClaim.Parse("""{"allowed":false,"unlimited":false,"used":3,"limit":3,"resetsAt":"2026-11-03T00:00:00.000Z"}"""));
    }

    private static string Note(DateTime resetsAt) =>
        Loc.F("Usage.Limit.Reached", 3, resetsAt.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));

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

    /// <summary>The site's token, profile and usage endpoints in miniature.</summary>
    private sealed class FakeUsageSite : HttpMessageHandler
    {
        public const string Base = "https://site.test/api/v1/";

        public bool Enabled { get; set; } = true;

        public bool Allowed { get; set; } = true;

        public bool Offline { get; set; }

        public bool Subscriber { get; set; }

        public List<(string Feature, string GameId)> Claims { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            if (Offline && path.StartsWith("usage", StringComparison.Ordinal))
            {
                throw new HttpRequestException("offline");
            }

            switch (path)
            {
                case "oauth/token":
                    return Json("""{"access_token":"acc","token_type":"Bearer","refresh_token":"ref-2","expires_in":900}""");
                case "me":
                    return Json(Subscriber
                        ? """{"user":{"id":"u-1","name":"Player"},"subscription":{"hasAccess":true,"source":"boosty","entitlements":{"battlegrounds":true}}}"""
                        : """{"user":{"id":"u-1","name":"Player"}}""");
                case "usage/limits":
                    return Json("""{"enabled":ENABLED,"day":"2026-11-02","resetsAt":"2026-11-03T00:00:00.000Z","features":{"bg.poolStats":{"section":"battlegrounds","freePerDay":3}}}"""
                        .Replace("ENABLED", Enabled ? "true" : "false", StringComparison.Ordinal));
                case "usage/claim":
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    {
                        Claims.Add((doc.RootElement.GetProperty("feature").GetString()!, doc.RootElement.GetProperty("gameId").GetString()!));
                    }

                    return Json($$"""{"feature":"bg.poolStats","allowed":{{(Allowed ? "true" : "false")}},"unlimited":false,"used":{{Claims.Count}},"limit":3,"resetsAt":"2026-11-03T00:00:00.000Z"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
