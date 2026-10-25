using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Arena draft helper (docs/arena-draft.md): ArenaSmith scores and class results parse from real answers of the
/// Hearthpulse API (Fixtures/Arena, trimmed to a few cards), load over a fake server and from the disk cache offline,
/// and the draft view is built from Arena.log lines and the offer the memory reader would see.
/// </summary>
public sealed class ArenaDraftTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "hp-arena-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void Card_scores_parse_from_the_real_answer()
    {
        var (cards, fetchedAt) = HearthpulseData.ParseArenaCards(Data("arena-cards.json"));

        Assert.Equal(5, cards.Count);
        var card = cards.Single(c => c.CardId == "BE_090");
        Assert.Equal(129824, card.DbfId);
        Assert.Equal(54.0, card.Score);
        Assert.Equal("S", card.Tier);
        Assert.Equal(0.631, card.WinRate!.Value, 3);
        Assert.Equal(0.9784, card.PickRate!.Value, 4);
        Assert.Equal(21296, card.Games);
        Assert.Null(cards.Single(c => c.CardId == "VAC_301").Score);
        Assert.Equal(new DateTime(2026, 10, 25, 12, 45, 10, DateTimeKind.Utc), fetchedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_cache_keeps_a_small_copy_that_parses_the_same()
    {
        var data = Data("arena-cards.json");
        var small = JsonSerializer.SerializeToElement(HearthpulseData.ShrinkArenaCards(data));

        var full = HearthpulseData.ParseArenaCards(data);
        var cached = HearthpulseData.ParseArenaCards(small);

        Assert.Equal(full.Cards, cached.Cards);
        Assert.Equal(full.FetchedAt, cached.FetchedAt);
        Assert.True(small.GetRawText().Length < data.GetRawText().Length / 4);
    }

    [Fact]
    public void Class_results_parse_from_the_real_answer()
    {
        var classes = HearthpulseData.ParseArenaClasses(Data("arena-classes.json"));

        Assert.Equal(11, classes.Count);
        var warlock = classes.Single(c => c.PlayerClass == "WARLOCK");
        Assert.Equal(0.5542, warlock.WinRate, 4);
        Assert.Equal(0.1436, warlock.SevenPlus!.Value, 4);
        Assert.Equal(2626, warlock.Drafts);
        Assert.Contains(classes, c => c.PlayerClass == "DEMONHUNTER");
        Assert.Contains(classes, c => c.PlayerClass == "DEATHKNIGHT");
    }

    [Theory]
    [InlineData(55.0, "S")]
    [InlineData(53.0, "S")]
    [InlineData(52.9, "A")]
    [InlineData(45.0, "A")]
    [InlineData(35.0, "B")]
    [InlineData(24.0, "C")]
    [InlineData(10.0, "D")]
    [InlineData(1.2, "E")]
    public void Tiers_follow_the_site(double score, string tier) => Assert.Equal(tier, ArenaMeta.TierOf(score));

    [Fact]
    public async Task Meta_loads_over_the_api_and_offline_from_the_cache()
    {
        var server = new FixtureServer(ok: true);
        var meta = await Data(server).LoadArenaMetaAsync();

        Assert.Equal(5, meta.CardCount);
        Assert.Equal(54.0, meta.Score("BE_085"));
        Assert.Equal("WARLOCK", meta.Classes[0].PlayerClass);
        Assert.Contains(server.Requests, r => r.EndsWith(HearthpulseData.ArenaCardsPath, StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.EndsWith(HearthpulseData.ArenaClassesPath, StringComparison.Ordinal));
        Assert.True(new FileInfo(Path.Combine(_cache, "arena-cards.json")).Length < 4000);

        // The server goes down and the cache is old: the stale copy is served.
        foreach (var file in Directory.GetFiles(_cache))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        }

        var offline = await Data(new FixtureServer(ok: false)).LoadArenaMetaAsync();
        Assert.Equal(5, offline.CardCount);
        Assert.Equal(11, offline.Classes.Count);
    }

    [Fact]
    public async Task Nothing_cached_and_no_server_gives_an_empty_meta()
    {
        var meta = await Data(new FixtureServer(ok: false)).LoadArenaMetaAsync();

        Assert.False(meta.HasCards);
        Assert.Empty(meta.Classes);
    }

    [Fact]
    public void Arena_log_follows_the_hero_and_picks()
    {
        var tracker = new ArenaDraftTracker();
        foreach (var line in DraftLog)
        {
            tracker.Feed(line);
        }

        Assert.Equal("HERO_07", tracker.State.HeroCardId);
        Assert.Equal(["BE_085", "BE_193", "BE_085"], tracker.State.Cards);
        Assert.Equal("DRAFTING", tracker.State.Mode);

        // Opening the Arena screen again lists the deck from scratch.
        tracker.Feed("D 21:05:00.0000000 DraftManager.OnChoicesAndContents - Draft Deck ID: 4242, Hero Card = HERO_07");
        tracker.Feed("D 21:05:00.0000000 DraftManager.OnChoicesAndContents - Draft deck contains card BE_090");
        Assert.Equal(4242, tracker.State.DeckId);
        Assert.Equal(["BE_090"], tracker.State.Cards);
    }

    [Fact]
    public void A_card_offer_gets_scores_and_marks_the_best()
    {
        var view = ArenaDraft.Build(State(["BE_193"]), ["TIME_063", "BE_090", "VAC_301"], Meta(), Cards);

        Assert.False(view.HeroPick);
        Assert.Equal("WARLOCK", view.PlayerClass);
        Assert.Equal(["TIME_063", "BE_090", "VAC_301"], view.Options.Select(o => o.CardId));
        Assert.Equal([false, true, false], view.Options.Select(o => o.IsBest));
        Assert.Equal("E", view.Options[0].Tier);
        Assert.Null(view.Options[2].Score);
        Assert.Empty(view.Classes);
    }

    [Fact]
    public void A_hero_offer_shows_class_win_rates()
    {
        var view = ArenaDraft.Build(ArenaDraftState.Empty, ["HERO_07", "HERO_08", "HERO_10"], Meta(), Cards);

        Assert.True(view.HeroPick);
        Assert.Equal(["WARLOCK", "MAGE", "DEMONHUNTER"], view.Options.Select(o => o.PlayerClass));
        Assert.Equal([true, false, false], view.Options.Select(o => o.IsBest));
        Assert.Equal(0.5542, view.Options[0].WinRate!.Value, 4);
        Assert.Equal(11, view.Classes.Count);
        Assert.Equal(3, view.Classes.Count(c => c.Offered));
    }

    [Fact]
    public void The_deck_so_far_has_a_curve_and_an_average()
    {
        var view = ArenaDraft.Build(State(["BE_085", "BE_085", "BE_193", "VAC_301"]), null, Meta(), Cards);

        Assert.Equal(4, view.Picks);
        Assert.Equal(3, view.Deck.Count);
        Assert.Equal(2, view.Deck.Single(c => c.CardId == "BE_085").Count);
        Assert.Equal([0, 1, 2, 0, 0, 0, 0, 1], view.Score.Curve);
        Assert.Equal((54 + 54 + 31) / 3.0, view.Score.Average!.Value, 6);
        Assert.Equal("A", view.Score.Tier);
        Assert.Equal(3, view.Score.Scored);
        Assert.Empty(view.Options);
    }

    [Fact]
    public void A_deck_code_is_scored()
    {
        var deck = new DeckDefinition("Arena", 1, "", "FT_WILD", [], new Dictionary<int, int> { [129824] = 1, [129793] = 2 });

        var score = ArenaDraft.ScoreOf(deck, Meta(), Cards);

        Assert.Equal(3, score.Cards);
        Assert.Equal(54.0, score.Average);
    }

    [Fact]
    public void The_session_shows_the_draft_while_the_screen_is_open_and_hides_a_picked_offer()
    {
        var now = new DateTime(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc);
        // SQLite keeps the file open in its pool, so it lives outside the folder Dispose deletes (like DecksLogTests).
        var db = Path.Combine(Path.GetTempPath(), $"hp-arena-{Guid.NewGuid():N}.db");
        using var memory = new HearthstoneMemory();
        var session = new GameSession(new HistoryStore(db), memory)
        {
            Clock = () => now,
            IsHearthstoneRunning = () => true,
            ReadsMemory = false,
            Cards = Cards,
        };
        session.SetArenaMeta(Meta());

        session.OnLines("LoadingScreen.log", ["D 21:00:00.0000000 LoadingScreen.OnSceneLoaded() - prevMode=HUB currMode=DRAFT"], catchingUp: false);
        session.OnLines("Arena.log", DraftLog, catchingUp: false);
        session.SetDraftOffer(["TIME_063", "BE_090", "VAC_301"]);
        session.OnTick();

        var draft = session.Current.ArenaDraft;
        Assert.NotNull(draft);
        Assert.Equal(3, draft.Picks);
        Assert.Equal("BE_090", draft.Options.Single(o => o.IsBest).CardId);

        // The player takes the best card; until the client deals the next offer the old one is not shown again.
        session.OnLines("Arena.log", ["D 21:01:00.0000000 Client chooses: Card (BE_090)"], catchingUp: false);
        now = now.AddSeconds(1);
        session.OnTick();
        Assert.Empty(session.Current.ArenaDraft!.Options);
        Assert.Equal(4, session.Current.ArenaDraft.Picks);

        session.SetDraftOffer(["BE_193", "BE_085", "TIME_063"]);
        now = now.AddSeconds(1);
        session.OnTick();
        Assert.Equal(3, session.Current.ArenaDraft!.Options.Count);

        // Leaving the draft screen hides the panels.
        session.OnLines("LoadingScreen.log", ["D 21:02:00.0000000 LoadingScreen.OnSceneLoaded() - prevMode=DRAFT currMode=HUB"], catchingUp: false);
        now = now.AddSeconds(1);
        session.OnTick();
        Assert.Null(session.Current.ArenaDraft);
    }

    private static readonly string[] DraftLog =
    [
        "D 21:00:01.0000000 SetDraftMode - DRAFTING",
        "D 21:00:02.0000000 DraftManager.OnChosen(): hero=HERO_07 premium=NORMAL",
        "D 21:00:03.0000000 Client chooses: Ящик Пандоры (BE_085)",
        "D 21:00:04.0000000 Client chooses: Some (Card) Name (BE_193)",
        "D 21:00:05.0000000 Client chooses: Ящик Пандоры (BE_085)",
    ];

    private static ArenaDraftState State(IReadOnlyList<string> cards) => new("HERO_07", 0, cards, "DRAFTING");

    private static ArenaMeta Meta()
    {
        var cards = HearthpulseData.ParseArenaCards(Data("arena-cards.json")).Cards;
        return new ArenaMeta(cards, HearthpulseData.ParseArenaClasses(Data("arena-classes.json")));
    }

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [
          {"id":"BE_090","dbfId":129824,"name":"Big Warlock Card","cost":6,"type":"MINION","cardClass":"WARLOCK","collectible":true},
          {"id":"BE_085","dbfId":129793,"name":"Small Warlock Spell","cost":2,"type":"SPELL","cardClass":"WARLOCK","collectible":true},
          {"id":"BE_193","dbfId":129901,"name":"Neutral One","cost":1,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"TIME_063","dbfId":120001,"name":"Weak Neutral","cost":3,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"VAC_301","dbfId":120002,"name":"Unscored","cost":7,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"HERO_07","dbfId":893,"name":"Gul'dan","type":"HERO","cardClass":"WARLOCK"},
          {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
          {"id":"HERO_10","dbfId":56550,"name":"Illidan","type":"HERO","cardClass":"DEMONHUNTER"}
        ]
        """)));

    private HearthpulseData Data(FixtureServer server) =>
        new(new HearthpulseApi(new HttpClient(server), HearthpulseApi.DefaultBaseUrl), _cache);

    /// <summary>What <see cref="HearthpulseApi"/> hands the parser: the answer's <c>data</c>.</summary>
    private static JsonElement Data(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arena", name)));
        return doc.RootElement.GetProperty("data").Clone();
    }

    private sealed class FixtureServer(bool ok) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests)
            {
                Requests.Add(path);
            }

            if (!ok)
            {
                throw new HttpRequestException("offline");
            }

            var file = path.Contains("datasets/hsreplay_arena_cards_advanced", StringComparison.Ordinal) ? "arena-cards.json"
                : path.Contains("v1/arena/classes", StringComparison.Ordinal) ? "arena-classes.json"
                : null;
            return Task.FromResult(file is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\":\"Not Found\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arena", file)), Encoding.UTF8, "application/json"),
                });
        }
    }
}
