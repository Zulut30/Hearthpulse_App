using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Twitch;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

/// <summary>
/// The Twitch extension's sender (TASK-2026-11-07-twitch-extension, AC1–AC4, AC9): what a snapshot becomes, and when and
/// how often it reaches a fake site, on a fake clock.
/// </summary>
public sealed class TwitchPublisherTests : IDisposable
{
    private static readonly Uri Base = new("https://site.test/api/v1/");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSite _site = new();
    private readonly AppSettings _settings = new() { TwitchExtension = true };
    private readonly HttpClient _http;
    private string? _token = "access-1";
    private TwitchPublisher? _publisher;

    public TwitchPublisherTests() => _http = new HttpClient(_site);

    public void Dispose()
    {
        _publisher?.Dispose();
        _http.Dispose();
    }

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
        """[{"id":"CORE_CS2_106","dbfId":69,"name":"Fiery War Axe","cost":3},{"id":"EX1_410","dbfId":1023,"name":"Shield Slam","cost":1}]""")));

    private TwitchPublisher Publisher() =>
        _publisher = new TwitchPublisher(new TwitchClient(_http, Base), _ => Task.FromResult(_token), _settings, () => Cards, _time);

    private static IReadOnlyList<DeckCardRow> Rows(int axes = 2, int slams = 1) =>
    [
        new("EX1_410", "Shield Slam", 1, slams, 2, "EPIC", 0.1, false),
        new("CORE_CS2_106", "Fiery War Axe", 3, axes, 2, "COMMON", 0.1, false),
    ];

    private static ConstructedView View(IReadOnlyList<DeckCardRow>? rows = null, int turn = 5, bool known = true) =>
        new(turn, "Pirate Warrior", known, "HERO_01", "WARRIOR", "HERO_08", "MAGE", "Rival#1234", rows ?? Rows(), 22, 4,
            [new PlayedCard("CS2_029", "Fireball", 4, 1, 3, false)],
            [new HandSlot(1, 2, "CS2_032", "Flamestrike", false, false, null)],
            18, 6, 1, new RankInfo("FT_STANDARD", 5, 40, 0, 0), new RankInfo("FT_STANDARD", 5, 41, 0, 0))
        {
            FormatType = "FT_STANDARD",
        };

    private static LiveSnapshot Game(ConstructedView? view = null, string gameType = "GT_RANKED") =>
        LiveSnapshot.Empty with { InGame = true, GameType = gameType, Constructed = view ?? View(), HearthstoneRunning = true };

    private static LiveSnapshot Menu => LiveSnapshot.Empty with { HearthstoneRunning = true };

    /// <summary>Lets the step under way finish (the fake site answers at once, but a step may hop a thread).</summary>
    private async Task SettleAsync()
    {
        for (var i = 0; i < 200 && _publisher!.Busy; i++)
        {
            await Task.Delay(5);
        }

        Assert.False(_publisher!.Busy);
    }

    private async Task AdvanceAsync(double seconds)
    {
        // Whole seconds one by one, so every timer due on the way fires at its time.
        for (var left = seconds; left > 0; left -= 1)
        {
            _time.Advance(TimeSpan.FromSeconds(Math.Min(1, left)));
            await SettleAsync();
        }
    }

    private async Task<TwitchPublisher> StartedAsync(LiveSnapshot? first = null)
    {
        var publisher = Publisher();
        publisher.Update(first ?? Game());
        await SettleAsync();
        return publisher;
    }

    // ---- What a snapshot becomes (AC3, AC9)

    [Fact]
    public void State_carries_only_the_own_decks_remaining_cards_in_the_trackers_order()
    {
        var json = TwitchState.From(Game(), Cards).ToJson(42);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(["v", "seq", "mode", "deck", "game"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal(42, root.GetProperty("seq").GetInt64());
        Assert.Equal("constructed", root.GetProperty("mode").GetString());
        var deck = root.GetProperty("deck");
        Assert.Equal(["name", "hero", "format", "left", "cards"], deck.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Pirate Warrior", deck.GetProperty("name").GetString());
        Assert.Equal("HERO_01", deck.GetProperty("hero").GetString());
        Assert.Equal("standard", deck.GetProperty("format").GetString());
        Assert.Equal(22, deck.GetProperty("left").GetInt32());
        var cards = deck.GetProperty("cards").EnumerateArray().ToList();
        Assert.Equal(["EX1_410", "CORE_CS2_106"], cards.Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(["id", "dbf", "cost", "name", "left", "total"], cards[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(1023, cards[0].GetProperty("dbf").GetInt32());
        Assert.Equal(69, cards[1].GetProperty("dbf").GetInt32());
        Assert.Equal(2, cards[1].GetProperty("left").GetInt32());
        Assert.Equal(["turn"], root.GetProperty("game").EnumerateObject().Select(p => p.Name));
        Assert.Equal(5, root.GetProperty("game").GetProperty("turn").GetInt32());

        // AC9: nothing of the opponent (name, class, hero, hand, played cards, rank) and nothing of the own hand.
        foreach (var hidden in new[] { "Rival", "MAGE", "HERO_08", "CS2_029", "Fireball", "CS2_032", "Flamestrike", "rank", "hand", "opponent" })
        {
            Assert.DoesNotContain(hidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Names_stay_readable_and_unknown_deck_name_and_hero_are_null()
    {
        var view = View([new DeckCardRow("CORE_CS2_106", "Боевой топор", 3, 1, 2, "COMMON", 0.1, false)]) with { DeckName = null, PlayerHeroCardId = string.Empty };
        var json = TwitchState.From(Game(view), CardDb.Empty).ToJson(1);
        Assert.Contains("Боевой топор", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"hero\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"dbf\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void No_game_battlegrounds_arena_draft_and_an_unknown_deck_are_none()
    {
        var bg = Game() with { Battlegrounds = StreamScene.Lobby(CardDb.Empty), GameType = "GT_BATTLEGROUNDS" };
        var draft = Menu with { ArenaDraft = new Hearthpulse.Core.Arena.ArenaDraftView(null, null, 0, [], false, [], Hearthpulse.Core.Arena.ArenaDeckScore.Empty, [], false) };
        foreach (var snapshot in new[] { LiveSnapshot.Empty, Menu, bg, draft, Game(View(known: false)), Game() with { InGame = false } })
        {
            var state = TwitchState.From(snapshot, Cards);
            Assert.Same(TwitchState.None, state);
            Assert.Equal("""{"v":1,"seq":7,"mode":"none"}""", state.ToJson(7));
        }
    }

    [Theory]
    [InlineData("GT_RANKED", "FT_STANDARD", "standard")]
    [InlineData("GT_RANKED", "FT_WILD", "wild")]
    [InlineData("GT_RANKED", "FT_TWIST", "twist")]
    [InlineData("GT_VS_FRIEND", "FT_CLASSIC", "classic")]
    [InlineData("GT_CASUAL", "FT_WILD", "casual")]
    [InlineData("GT_ARENA", "FT_WILD", "other")]
    [InlineData("GT_TAVERNBRAWL", null, "other")]
    [InlineData("GT_RANKED", null, "other")]
    public void Format_follows_the_contract(string gameType, string? format, string expected) =>
        Assert.Equal(expected, TwitchState.FormatOf(gameType, format));

    // ---- When it is sent (AC1, AC2, AC3)

    [Fact]
    public async Task Asks_the_link_then_sends_with_the_accounts_token()
    {
        await StartedAsync();

        Assert.Equal(["GET twitch/status", "POST twitch/state"], _site.Calls);
        Assert.All(_site.Requests, r => Assert.Equal("Bearer access-1", r.Auth));
        Assert.Equal("application/json", _site.Requests[1].ContentType);
        Assert.Contains("\"mode\":\"constructed\"", _site.Posts[0], StringComparison.Ordinal);
        var link = _publisher!.Link;
        Assert.Equal(TwitchLinkState.Connected, link.State);
        Assert.Equal("streamer", link.Login);
        Assert.Equal(new Uri("https://site.test/account/twitch"), link.LinkUrl);
    }

    [Fact]
    public async Task Sends_only_changes_at_most_once_a_second_and_the_last_of_a_burst()
    {
        var publisher = await StartedAsync();

        publisher.Update(Game());
        publisher.Update(Game());
        await SettleAsync();
        Assert.Single(_site.Posts);

        // Three changes within the second: one send when it ends, with the last.
        publisher.Update(Game(View(Rows(axes: 1))));
        publisher.Update(Game(View(Rows(axes: 1, slams: 0))));
        publisher.Update(Game(View(Rows(axes: 0, slams: 0))));
        await SettleAsync();
        Assert.Single(_site.Posts);
        await AdvanceAsync(1);
        Assert.Equal(2, _site.Posts.Count);
        Assert.Contains("\"id\":\"CORE_CS2_106\",\"dbf\":69,\"cost\":3,\"name\":\"Fiery War Axe\",\"left\":0", _site.Posts[1], StringComparison.Ordinal);

        // Numbers keep growing.
        Assert.True(Seq(_site.Posts[1]) > Seq(_site.Posts[0]));
    }

    [Fact]
    public async Task An_unchanged_deck_is_sent_again_every_fifteen_seconds()
    {
        await StartedAsync();

        await AdvanceAsync(14);
        Assert.Single(_site.Posts);
        await AdvanceAsync(1);
        Assert.Equal(2, _site.Posts.Count);
        Assert.Equal(Body(_site.Posts[0]), Body(_site.Posts[1]));
        await AdvanceAsync(15);
        Assert.Equal(3, _site.Posts.Count);
    }

    [Fact]
    public async Task The_delay_holds_every_change_back_for_its_seconds()
    {
        _settings.TwitchDelaySeconds = 30;
        var publisher = Publisher();
        publisher.Update(Game());
        await SettleAsync();
        Assert.Equal(["GET twitch/status"], _site.Calls);

        await AdvanceAsync(29);
        Assert.Empty(_site.Posts);
        await AdvanceAsync(1);
        Assert.Single(_site.Posts);

        // A card drawn at +40 goes out at +70, not before; the keep-alive meanwhile repeats the old deck.
        await AdvanceAsync(10);
        publisher.Update(Game(View(Rows(axes: 1))));
        await AdvanceAsync(29);
        Assert.All(_site.Posts, p => Assert.Contains("\"left\":2,\"total\":2", p, StringComparison.Ordinal));
        await AdvanceAsync(1);
        Assert.Contains("\"name\":\"Fiery War Axe\",\"left\":1", _site.Posts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shorter_delay_lets_the_newest_state_replace_older_ones_still_waiting()
    {
        _settings.TwitchDelaySeconds = 60;
        var publisher = Publisher();
        publisher.Update(Game());
        await SettleAsync();

        _settings.TwitchDelaySeconds = 0;
        publisher.Update(Menu);
        await SettleAsync();
        Assert.Equal(["""{"mode":"none"}"""], _site.Posts.Select(Body));
        await AdvanceAsync(61);
        Assert.Single(_site.Posts);
    }

    [Fact]
    public async Task Leaving_the_game_sends_none_once_and_no_keep_alive()
    {
        var publisher = await StartedAsync();

        publisher.Update(Menu);
        await AdvanceAsync(1);
        Assert.Equal("""{"mode":"none"}""", Body(_site.Posts[^1]));
        publisher.Update(Game() with { Battlegrounds = StreamScene.Lobby(CardDb.Empty) });
        publisher.Update(Menu);
        await AdvanceAsync(60);
        Assert.Equal(2, _site.Posts.Count);
    }

    [Fact]
    public async Task Off_sends_nothing_and_switching_off_clears_the_channel_once()
    {
        _settings.TwitchExtension = false;
        var publisher = Publisher();
        publisher.Update(Game());
        await AdvanceAsync(30);
        Assert.Empty(_site.Calls);
        Assert.Equal(TwitchLinkState.Off, publisher.Link.State);

        _settings.TwitchExtension = true;
        publisher.Kick();
        publisher.Update(Game());
        await SettleAsync();
        Assert.Single(_site.Posts);

        _settings.TwitchExtension = false;
        publisher.Kick();
        await SettleAsync();
        Assert.Equal("""{"mode":"none"}""", Body(_site.Posts[^1]));
        Assert.Equal(TwitchLinkState.Off, publisher.Link.State);
        publisher.Update(Game());
        await AdvanceAsync(60);
        Assert.Equal(2, _site.Posts.Count);
    }

    // ---- When it stops (AC4)

    [Fact]
    public async Task Signed_out_sends_nothing_and_asks_again_at_most_once_a_minute()
    {
        _token = null;
        var publisher = await StartedAsync();
        Assert.Empty(_site.Calls);
        Assert.Equal(TwitchLinkState.SignedOut, publisher.Link.State);

        publisher.Update(Game(View(Rows(axes: 1))));
        await AdvanceAsync(59);
        Assert.Empty(_site.Calls);

        // Signing in (the host kicks on it) starts at once.
        _token = "access-2";
        publisher.Kick();
        await SettleAsync();
        Assert.Equal(["GET twitch/status", "POST twitch/state"], _site.Calls);
        Assert.Equal(TwitchLinkState.Connected, publisher.Link.State);
    }

    [Fact]
    public async Task Not_linked_stops_until_the_link_says_linked()
    {
        _site.Linked = false;
        var publisher = await StartedAsync();
        Assert.Equal(["GET twitch/status"], _site.Calls);
        Assert.Equal(TwitchLinkState.NotLinked, publisher.Link.State);
        Assert.Equal(new Uri("https://site.test/account/twitch"), publisher.Link.LinkUrl);

        await AdvanceAsync(59);
        Assert.Single(_site.Calls);
        await AdvanceAsync(1);
        Assert.Equal(2, _site.Calls.Count);

        // Linked on the site meanwhile: the next check finds it and the deck goes.
        _site.Linked = true;
        await AdvanceAsync(60);
        Assert.Equal(["GET twitch/status", "GET twitch/status", "GET twitch/status", "POST twitch/state"], _site.Calls);
        Assert.Equal(TwitchLinkState.Connected, publisher.Link.State);
    }

    [Theory]
    [InlineData(404, "twitch_not_linked", TwitchLinkState.NotLinked, null)]
    [InlineData(503, "twitch_not_configured", TwitchLinkState.NotConfigured, null)]
    [InlineData(503, "twitch_unavailable", TwitchLinkState.Error, "TWITCH_UNAVAILABLE")]
    [InlineData(401, "invalid_token", TwitchLinkState.SignedOut, null)]
    [InlineData(413, "payload_too_large", TwitchLinkState.Error, "PAYLOAD_TOO_LARGE")]
    [InlineData(400, "invalid_state", TwitchLinkState.Error, "INVALID_STATE")]
    [InlineData(500, null, TwitchLinkState.Error, "SERVER_ERROR")]
    public async Task A_refusal_stops_sending_for_a_minute(int status, string? code, TwitchLinkState state, string? shownCode)
    {
        var publisher = await StartedAsync();
        _site.StateAnswer = (status, code, null);

        publisher.Update(Game(View(Rows(axes: 1))));
        await AdvanceAsync(1);
        Assert.Equal(2, _site.Posts.Count);
        Assert.Equal(state, publisher.Link.State);
        Assert.Equal(shownCode, publisher.Link.Code);

        // Changes and keep-alives meanwhile do not reach the site.
        publisher.Update(Game(View(Rows(axes: 0))));
        await AdvanceAsync(58);
        Assert.Equal(2, _site.Posts.Count);
        var calls = _site.Calls.Count;

        // A minute on: the link is asked first, then the latest deck.
        _site.StateAnswer = (204, null, null);
        await AdvanceAsync(2);
        Assert.Equal(["GET twitch/status", "POST twitch/state"], _site.Calls.Skip(calls));
        Assert.Contains("\"name\":\"Fiery War Axe\",\"left\":0", _site.Posts[^1], StringComparison.Ordinal);
        Assert.Equal(TwitchLinkState.Connected, publisher.Link.State);
    }

    [Fact]
    public async Task Offline_is_an_error_and_tried_again_after_a_minute()
    {
        _site.Offline = true;
        var publisher = await StartedAsync();
        Assert.Equal(TwitchLinkState.Error, publisher.Link.State);
        Assert.Equal("OFFLINE", publisher.Link.Code);
        Assert.Single(_site.Calls);

        await AdvanceAsync(59);
        Assert.Single(_site.Calls);
        _site.Offline = false;
        await AdvanceAsync(1);
        Assert.Equal(["GET twitch/status", "GET twitch/status", "POST twitch/state"], _site.Calls);
    }

    [Fact]
    public async Task A_status_that_says_not_configured_stops_before_any_send()
    {
        _site.Configured = false;
        var publisher = await StartedAsync();
        Assert.Equal(TwitchLinkState.NotConfigured, publisher.Link.State);
        Assert.Empty(_site.Posts);
    }

    [Fact]
    public async Task Rate_limited_waits_the_sites_retry_after()
    {
        var publisher = await StartedAsync();
        _site.StateAnswer = (429, "rate_limited", 3);

        publisher.Update(Game(View(Rows(axes: 1))));
        await AdvanceAsync(1);
        Assert.Equal(2, _site.Posts.Count);
        _site.StateAnswer = (204, null, null);
        await AdvanceAsync(2);
        Assert.Equal(2, _site.Posts.Count);
        await AdvanceAsync(1);
        Assert.Equal(3, _site.Posts.Count);
        Assert.Equal(TwitchLinkState.Connected, publisher.Link.State);
    }

    [Fact]
    public async Task A_longer_retry_after_than_a_minute_is_kept()
    {
        var publisher = await StartedAsync();
        _site.StateAnswer = (503, "twitch_unavailable", 120);

        publisher.Update(Game(View(Rows(axes: 1))));
        await AdvanceAsync(1);
        var calls = _site.Calls.Count;
        _site.StateAnswer = (204, null, null);
        await AdvanceAsync(118);
        Assert.Equal(calls, _site.Calls.Count);
        await AdvanceAsync(2);
        Assert.True(_site.Calls.Count > calls);
    }

    [Fact]
    public void Status_parses_the_contract_and_ignores_a_bad_link()
    {
        var info = TwitchClient.ParseStatus("""{"configured":true,"linked":true,"login":"streamer","displayName":"Streamer","linkUrl":"https://hearthpulse.net/account/twitch"}""");
        Assert.Equal(new TwitchStatusInfo(true, true, "streamer", "Streamer", new Uri("https://hearthpulse.net/account/twitch")), info);
        Assert.Null(TwitchClient.ParseStatus("""{"configured":true,"linked":false,"linkUrl":"javascript:alert(1)"}""")!.LinkUrl);
        Assert.Null(TwitchClient.ParseStatus("not json"));
        Assert.Null(TwitchClient.ParseStatus("[]"));
    }

    private static long Seq(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("seq").GetInt64();
    }

    /// <summary>The body without v and seq, as <see cref="TwitchState.Key"/>.</summary>
    private static string Body(string json)
    {
        var at = json.IndexOf("\"mode\"", StringComparison.Ordinal);
        return "{" + json[at..];
    }

    private sealed record Seen(string Call, string? Auth, string? ContentType);

    private sealed class FakeSite : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<Seen> Requests { get; } = [];

        public List<string> Calls { get { lock (_gate) { return Requests.Select(r => r.Call).ToList(); } } }

        public List<string> Posts { get; } = [];

        public bool Configured { get; set; } = true;

        public bool Linked { get; set; } = true;

        public bool Offline { get; set; }

        public (int Status, string? Code, int? RetryAfter) StateAnswer { get; set; } = (204, null, null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                Requests.Add(new Seen(request.Method.Method + " " + path, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType));
                if (body is not null)
                {
                    Posts.Add(body);
                }
            }

            if (Offline)
            {
                throw new HttpRequestException("no network");
            }

            if (path == "twitch/status")
            {
                var linked = Linked ? ",\"login\":\"streamer\",\"displayName\":\"Streamer\"" : string.Empty;
                return Json(200, $$"""{"configured":{{(Configured ? "true" : "false")}},"linked":{{(Linked ? "true" : "false")}}{{linked}},"linkUrl":"https://site.test/account/twitch"}""");
            }

            var (status, code, retry) = StateAnswer;
            var response = status == 204
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(status, code is null ? "{}" : $$$"""{"error":{"code":"{{{code}}}","message":"x"}}""");
            if (retry is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }

        private static HttpResponseMessage Json(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
