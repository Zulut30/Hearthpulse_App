using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Api;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The Hearthpulse API client and accounts: error envelopes become messages the player can act on, a session survives
/// a restart only while the server accepts it, and the community stats parse from the shapes the API serves.
/// </summary>
public sealed class HearthpulseApiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hp-api-" + Guid.NewGuid().ToString("N"));

    public HearthpulseApiTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string SessionReply = """
        {"data": {"access_token": "acc-1", "refresh_token": "ref-1", "token_type": "bearer", "expires_in": 3600,
                  "user": {"id": "u1", "email": "a@b.c", "display_name": "Anna", "created_at": "2026-10-01T12:00:00Z"}}}
        """;

    [Theory]
    [InlineData(401, """{"detail":{"code":"INVALID_TOKEN","message":"Missing or invalid API token"}}""", "INVALID_TOKEN")]
    [InlineData(404, """{"detail":"Not Found"}""", "NOT_AVAILABLE")]
    [InlineData(422, """{"detail":[{"loc":["body","email"],"msg":"field required"}]}""", "VALIDATION_ERROR")]
    [InlineData(503, "<html>down</html>", "SERVER_ERROR")]
    public void Errors_parse_from_every_envelope(int status, string body, string code)
    {
        var result = HearthpulseApi.Parse(status, body);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void Success_unwraps_data()
    {
        var result = HearthpulseApi.Parse(200, """{"data":[1,2],"meta":{}}""");

        Assert.True(result.Ok);
        Assert.Equal(2, result.Data.GetArrayLength());
    }

    [Fact]
    public async Task Sign_in_keeps_the_session_and_a_restart_restores_it()
    {
        var server = new FakeServer();
        server.Reply("v1/accounts/login", 200, SessionReply);
        server.Reply("v1/accounts/refresh", 200, SessionReply.Replace("acc-1", "acc-2"));
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));

        var account = new AccountService(server.Api, store);
        Assert.Null(await account.SignInAsync(" a@b.c ", "password1"));
        Assert.Equal("Anna", account.User!.DisplayName);
        Assert.Equal("acc-1", await account.GetAccessTokenAsync());
        Assert.Contains("\"email\":\"a@b.c\"", server.Bodies["v1/accounts/login"]);
        Assert.DoesNotContain("password1", File.ReadAllText(Path.Combine(_dir, "account.dat")));

        var restarted = new AccountService(server.Api, store);
        await restarted.RestoreAsync();
        Assert.True(restarted.SignedIn);
        Assert.Equal("acc-2", await restarted.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_rejected_session_is_forgotten_but_an_offline_one_is_kept()
    {
        var server = new FakeServer();
        server.Reply("v1/accounts/login", 200, SessionReply);
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        await new AccountService(server.Api, store).SignInAsync("a@b.c", "password1");

        server.Reply("v1/accounts/refresh", 0, string.Empty);
        var offline = new AccountService(server.Api, store);
        await offline.RestoreAsync();
        Assert.True(offline.SignedIn);

        server.Reply("v1/accounts/refresh", 401, """{"detail":{"code":"INVALID_TOKEN","message":"expired"}}""");
        var rejected = new AccountService(server.Api, store);
        await rejected.RestoreAsync();
        Assert.False(rejected.SignedIn);
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData(404, "", "NOT_AVAILABLE")]
    [InlineData(401, """{"detail":{"code":"INVALID_CREDENTIALS","message":"x"}}""", "INVALID_CREDENTIALS")]
    [InlineData(409, """{"detail":{"code":"EMAIL_TAKEN","message":"x"}}""", "EMAIL_TAKEN")]
    public async Task Failed_sign_in_explains_why(int status, string body, string code)
    {
        var server = new FakeServer();
        server.Reply("v1/accounts/register", status, body);
        var account = new AccountService(server.Api, new TokenStore(Path.Combine(_dir, "account.dat")));

        var error = await account.SignUpAsync("a@b.c", "password1", "Anna");

        Assert.Equal(code, error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(account.SignedIn);
    }

    [Fact]
    public async Task Sign_out_forgets_the_session_even_offline()
    {
        var server = new FakeServer();
        server.Reply("v1/accounts/login", 200, SessionReply);
        server.Reply("v1/accounts/logout", 0, string.Empty);
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        var account = new AccountService(server.Api, store);
        await account.SignInAsync("a@b.c", "password1");

        await account.SignOutAsync();

        Assert.False(account.SignedIn);
        Assert.Null(store.Load());
        Assert.Null(await account.GetAccessTokenAsync());
    }

    [Fact]
    public void Heroes_parse_from_the_battlegrounds_endpoint()
    {
        using var doc = JsonDocument.Parse("""
            [{"hero":"Lord Jaraxxus","dbfId":59807,"pick_rate":"55.07%","avg_placement":3.702,"tier":"S","id":"TB_BaconShop_HERO_37",
              "pick_rate_value":55.07,"placement_distribution":["22.00%","18.23%","13.63%","11.11%","9.30%","8.56%","8.80%","8.37%"]},
             {"hero":"Broken","id":"X","avg_placement":null}]
            """);

        var heroes = HearthpulseData.ParseHeroes(doc.RootElement);

        var hero = Assert.Single(heroes.Values);
        Assert.Equal(3.702, hero.AveragePosition, 3);
        Assert.Equal(0.5507, hero.PickRate, 4);
        Assert.Equal(0.6497, hero.Top4Rate, 4);
        Assert.Equal(0.22, hero.FirstRate, 4);
        Assert.Equal(0, hero.DataPoints);
    }

    [Fact]
    public void Minions_and_archetypes_parse()
    {
        using var minions = JsonDocument.Parse("""
            [{"card_id":"BG35_883","tavern_tier":6,"normal":{"impact":-0.4,"avg_placement_with":3.9,"games_with_minion":12034},"golden":{}}]
            """);
        using var archetypes = JsonDocument.Parse("""
            {"total":1,"archetypes":[{"archetype_id":56,"name":"Quest Priest","slug":"quest-priest","player_class":"PRIEST",
              "total_games":19342,"win_rate":53.59,"pct_of_total":11.04}]}
            """);

        var card = HearthpulseData.ParseMinions(minions.RootElement)["BG35_883"];
        var archetype = Assert.Single(HearthpulseData.ParseArchetypes(archetypes.RootElement));

        Assert.Equal(3.9, card.AveragePlacement);
        Assert.Equal(12034, card.DataPoints);
        Assert.Equal("PRIEST", archetype.Class);
        Assert.Equal(0.5359, archetype.WinRate, 4);
        Assert.Equal(10365, archetype.Wins);
        Assert.Equal(0.1104, archetype.Share, 4);
    }

    [Theory]
    [InlineData("legend", "LEGEND")]
    [InlineData("top-500-legend", "LEGEND")]
    [InlineData("diamond", "DIAMOND_THROUGH_LEGEND")]
    [InlineData("all", "ALL")]
    public void Ranks_map_to_hsreplay_ranges(string rank, string expected) =>
        Assert.Equal(expected, HearthpulseData.RankRange(rank));

    /// <summary>Answers by request path; status 0 means the network is down.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Dictionary<string, (int Status, string Body)> _replies = [];

        public FakeServer() => Api = new HearthpulseApi(new HttpClient(this), "https://api.test/");

        public HearthpulseApi Api { get; }

        public Dictionary<string, string> Bodies { get; } = [];

        public void Reply(string path, int status, string body) => _replies[path] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Content is not null)
            {
                Bodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (!_replies.TryGetValue(path, out var reply) || reply.Status == 0)
            {
                throw new HttpRequestException("offline");
            }

            return new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
        }
    }
}
