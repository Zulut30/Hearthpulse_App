using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Battlegrounds stats from the Hearthpulse API (docs/bg-meta-sources.md): the answers parse from real responses
/// (Fixtures/BgMeta, trimmed to a few heroes and minions), the meta loads its brackets over a fake server and its disk
/// cache, and the advisor works without the numbers only the stats pool gives (comps, trinkets, turn results).
/// </summary>
public sealed class BgMetaApiTests : IDisposable
{
    private const string Jaraxxus = "TB_BaconShop_HERO_37";

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "hp-bgmeta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void Hero_slice_parses_from_the_real_answer()
    {
        var slice = HearthpulseData.ParseHeroSlice(Fixture("heroes-top_50_percent.json"));

        Assert.Equal(new DateTime(2026, 10, 22, 2, 35, 2, DateTimeKind.Utc), slice.FetchedAt, TimeSpan.FromSeconds(1));
        var hero = slice.Heroes[Jaraxxus];
        Assert.Equal(3.702, hero.AveragePosition, 3);
        Assert.Equal(0.5507, hero.PickRate, 4);
        Assert.Equal(0.22, hero.FirstRate, 4);
        Assert.Equal(0.6497, hero.Top4Rate, 4);
        Assert.Equal(8, hero.PlacementShares!.Count);
        Assert.Equal(0.0837, hero.PlacementShares[7], 4);
        // Not in the API: left empty, never zero-filled.
        Assert.Equal(0, hero.DataPoints);
        Assert.Empty(hero.TribeImpact);
        Assert.Null(hero.CombatWinRate);
    }

    [Fact]
    public void Hero_slice_skips_broken_rows()
    {
        using var doc = JsonDocument.Parse("""
            {"fetched_at": "not a date", "heroes": [
              {"id": "GOOD", "avg_placement": 4.1, "pick_rate_value": 20, "placement_distribution": ["50%", "50%"]},
              {"id": "", "avg_placement": 4.0}, {"id": "NO_PLACE", "avg_placement": null}, "junk"]}
            """);

        var slice = HearthpulseData.ParseHeroSlice(doc.RootElement);

        var hero = Assert.Single(slice.Heroes.Values);
        Assert.Equal("GOOD", hero.HeroCardId);
        Assert.Null(hero.PlacementShares);
        Assert.Equal(default, slice.FetchedAt);
    }

    [Fact]
    public void Minions_parse_from_the_real_answer()
    {
        var minions = HearthpulseData.ParseMinions(Fixture("minions-all.json").GetProperty("data"));

        var titus = minions["BG25_354"];
        Assert.Equal(3.5133, titus.AveragePlacement, 4);
        Assert.Equal(51583, titus.DataPoints);
        Assert.Empty(titus.Turns);
    }

    [Fact]
    public async Task Meta_loads_the_api_brackets_and_nothing_it_does_not_have()
    {
        var server = FixtureServer.Full();

        var meta = await BattlegroundsMeta.LoadAsync(Data(server));

        Assert.NotNull(meta);
        Assert.Equal(BattlegroundsMeta.Attribution, meta.Source);
        Assert.Equal("Hearthpulse data", meta.Source);
        Assert.Equal(new[] { 50, 20, 5, 1 }, meta.HeroBrackets);
        Assert.Equal(new[] { 100, 50, 20 }, meta.CardsByBracket.Keys.OrderDescending());
        Assert.Equal(50, meta.WidestBracket);
        Assert.Empty(meta.Comps);
        Assert.Empty(meta.Trinkets);
        Assert.Empty(meta.Percentiles);
        Assert.False(meta.FromPool);

        // No MMR floors: the advisor uses the widest bracket whatever the rating.
        Assert.Equal(50, meta.BracketFor(null));
        Assert.Equal(50, meta.BracketFor(9000));
        Assert.Equal(2.984, meta.Hero(Jaraxxus, 1)!.AveragePosition, 3);
        // "All ranks" does not exist for heroes: the widest slice answers, and the caption says which one.
        Assert.Equal(3.702, meta.Hero(Jaraxxus, 100)!.AveragePosition, 3);
        Assert.Equal("top " + 0.5.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), BattlegroundsMeta.BracketLabel(meta.WidestBracket));
        Assert.Null(meta.CardAtTurn("BG25_354", 5, 50));
        Assert.Equal(51583, meta.Card("BG25_354")!.DataPoints);

        Assert.All(server.Requests, r => Assert.StartsWith("https://api.kolodahearthstone.com/", r));
        Assert.Contains(server.Requests, r => r.Contains("api/bg/heroes?mode=solo&mmr=TOP_1_PERCENT", StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.Contains("v1/battlegrounds/minions/stats?mmr=ALL&period=CURRENT_BATTLEGROUNDS_PATCH", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Requests, r => r.Contains("zerotoheroes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Meta_comes_from_the_disk_cache_and_keeps_the_slices_that_answered()
    {
        var server = FixtureServer.Full();
        server.Remove("mmr=TOP_5_PERCENT");
        server.Remove("minions/stats?mmr=TOP_20_PERCENT");

        var first = await BattlegroundsMeta.LoadAsync(Data(server));
        var requests = server.Requests.Count;
        var second = await BattlegroundsMeta.LoadAsync(Data(server));

        Assert.Equal(new[] { 50, 20, 1 }, first!.HeroBrackets);
        Assert.Equal(new[] { 100, 50 }, first.CardsByBracket.Keys.OrderDescending());
        // Answers (and the 404s) are cached for hours: the second load asks nothing.
        Assert.Equal(requests, server.Requests.Count);
        Assert.Equal(first.HeroBrackets, second!.HeroBrackets);
    }

    [Fact]
    public async Task No_meta_when_the_api_and_the_cache_have_no_heroes()
    {
        var server = new FixtureServer();

        Assert.Null(await BattlegroundsMeta.LoadAsync(Data(server)));
    }

    [Fact]
    public async Task Advisor_works_without_comps_trinkets_and_turn_results()
    {
        var meta = await BattlegroundsMeta.LoadAsync(Data(FixtureServer.Full()));
        var cards = Cards();
        var advisor = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Auto };
        var view = View() with
        {
            HeroOptions = [new BgHeroOption(Jaraxxus, "Jaraxxus", 0, null), new BgHeroOption("TB_BaconShop_HERO_49", "Millificent", 0, null), new BgHeroOption("UNKNOWN_HERO", "New", 0, null)],
        };
        view = view with
        {
            TrinketOptions = [new BgTrinketOption("BG30_MagicItem_433", "Trinket A"), new BgTrinketOption("BG30_MagicItem_434", "Trinket B")],
            DiscoverOptions = [new BgDiscoverOption("BG25_354", "Titus", 5, null), new BgDiscoverOption("BG35_883", "Balinda", 6, null)],
        };

        var advice = advisor.Enrich(view, 9000, cards);

        // Heroes are ranked from the API's top 50% slice (no tribe impact to adjust by).
        var best = Assert.Single(advice.HeroOptions, h => h.Recommended);
        Assert.Equal(Jaraxxus, best.CardId);
        Assert.Equal(3.702, best.Meta!.AveragePosition, 3);
        Assert.Equal(best.Meta.AveragePosition, best.Meta.AdjustedPosition);
        Assert.Empty(best.TopComps!);
        Assert.Null(advice.HeroOptions.Single(h => h.CardId == "UNKNOWN_HERO").Meta);
        // Nothing invented: no comps, no trinket numbers or pick, no community placement on cards.
        Assert.Empty(advice.Comps);
        Assert.All(advice.TrinketOptions, t => Assert.Null(t.Meta));
        Assert.DoesNotContain(advice.TrinketOptions, t => t.Recommended);
        Assert.Equal(2, advice.DiscoverAdvice.Count);
        Assert.All(advice.DiscoverAdvice, p => Assert.Null(p.Placement));
        Assert.All(advice.ShopAdvice, p => Assert.Null(p.Placement));
        Assert.Contains($"Hearthpulse data · top {0.5.ToString("P0", System.Globalization.CultureInfo.CurrentCulture)} ·", advice.MetaSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_lists_say_where_their_numbers_will_come_from()
    {
        var api = new BattlegroundsMeta(new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), [], [],
            new Dictionary<string, MetaTrinket>(), new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(), DateTime.UtcNow);
        var pooled = new BattlegroundsMeta(new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), [], [],
            new Dictionary<string, MetaTrinket>(), new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(), DateTime.UtcNow)
        {
            Source = $"{BattlegroundsMeta.Attribution} + {BattlegroundsMeta.PoolName}",
        };

        Assert.Equal("No comp stats yet: they will come from the Hearthpulse stats pool once it is running.", api.PoolPending("comp stats"));
        Assert.Equal("No trinket stats from the Hearthpulse stats pool yet.", pooled.PoolPending("trinket stats"));
    }

    private HearthpulseData Data(FixtureServer server) =>
        new(new HearthpulseApi(new HttpClient(server), HearthpulseApi.DefaultBaseUrl), _cache);

    private static JsonElement Fixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BgMeta", name)));
        return doc.RootElement.Clone();
    }

    private static CardDb Cards()
    {
        const string json = """
            [
              { "id": "TB_BaconShop_HERO_37", "dbfId": 59807, "name": "Lord Jaraxxus", "type": "HERO", "battlegroundsHero": true },
              { "id": "TB_BaconShop_HERO_49", "dbfId": 1, "name": "Millificent", "type": "HERO", "battlegroundsHero": true },
              { "id": "UNKNOWN_HERO", "dbfId": 2, "name": "New", "type": "HERO", "battlegroundsHero": true },
              { "id": "BG25_354", "dbfId": 97408, "name": "Titus Rivendare", "type": "MINION", "techLevel": 5, "isBattlegroundsPoolMinion": true },
              { "id": "BG35_883", "dbfId": 130298, "name": "Balinda Stonehearth", "type": "MINION", "techLevel": 6, "isBattlegroundsPoolMinion": true },
              { "id": "BG36_731", "dbfId": 132899, "name": "Imp-lusionist", "type": "MINION", "races": ["DEMON"], "techLevel": 4, "isBattlegroundsPoolMinion": true }
            ]
            """;
        return CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    private static BattlegroundsView View() => new(
        Turn: 7,
        TechLevel: 4,
        MaxTechLevel: 6,
        HeroCardId: Jaraxxus,
        Health: 30,
        Armor: 0,
        Place: 3,
        IsDuos: false,
        InCombat: false,
        IsHeroSelection: false,
        HeroOptions: [],
        Races: ["BEAST", "DEMON", "MURLOC", "PIRATE", "MECHANICAL"],
        RacesConfirmed: true,
        Opponents: [],
        Combats: [],
        TierHistory: [],
        Board: [new BgMinion("BG36_731", "Imp-lusionist", 4, 2, false, 4, [])],
        AnomalyCardId: null,
        Triples: 0,
        Shop: [new BgMinion("BG25_354", "Titus Rivendare", 1, 7, false, 5, []), new BgMinion("BG35_883", "Balinda Stonehearth", 6, 6, false, 6, [])],
        Hand: [])
    {
        Gold = 10,
    };

    /// <summary>Answers each request with the first route whose fragment the URL contains, else 404 like the API.</summary>
    private sealed class FixtureServer : HttpMessageHandler
    {
        private readonly List<(string Fragment, string Body)> _routes = [];

        public List<string> Requests { get; } = [];

        public static FixtureServer Full()
        {
            var server = new FixtureServer();
            foreach (var mmr in new[] { "TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT" })
            {
                server._routes.Add(($"api/bg/heroes?mode=solo&mmr={mmr}", File($"heroes-{mmr.ToLowerInvariant()}.json")));
            }

            foreach (var mmr in new[] { "ALL", "TOP_50_PERCENT", "TOP_20_PERCENT" })
            {
                server._routes.Add(($"minions/stats?mmr={mmr}", File($"minions-{mmr.ToLowerInvariant()}.json")));
            }

            return server;
        }

        public void Remove(string fragment) => _routes.RemoveAll(r => r.Fragment.Contains(fragment, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }

            var route = _routes.FirstOrDefault(r => url.Contains(r.Fragment, StringComparison.Ordinal));
            return Task.FromResult(route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\": \"Not Found\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") });
        }

        private static string File(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BgMeta", name));
    }
}
