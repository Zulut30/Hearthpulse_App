using System.IO.Compression;
using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

/// <summary>
/// Hearthstone Wiki pools from the Hearthpulse API (<c>api/v1/constructed-cards/{id}/wiki</c>), the first source of a
/// generator's pool. Fixtures are real answers of 2026-10-23 (Fixtures/CardWiki, gallery and sounds left out); the
/// catalog is the card pool fixture. Nothing here reaches the network.
/// </summary>
public sealed class CardPoolWikiTests
{
    private static readonly Lazy<CardPoolCatalog> Catalog = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return CardPoolCatalog.Read(gz)!;
    });

    private static readonly PoolContext DeathKnight = new("FT_STANDARD", "DEATHKNIGHT", "WARRIOR") { GameType = "GT_RANKED" };

    [Fact]
    public void A_wiki_answer_gives_each_pool_with_its_class_format_and_date()
    {
        var wiki = WikiPools.Parse(Fixture("BE_298"));

        Assert.Equal(["Discovered Blood Rune cards", "Discovered Frost Rune cards", "Discovered Unholy Rune cards"], wiki.Pools.Select(p => p.Name));
        Assert.Equal(["Blood Rune", "Frost Rune", "Unholy Rune"], wiki.Pools.Select(p => p.Label));
        Assert.Equal([29, 21, 33], wiki.Pools.Select(p => p.CardIds.Count));
        Assert.All(wiki.Pools, p => Assert.Equal(["DEATHKNIGHT"], p.HeroClasses));
        Assert.All(wiki.Pools, p => Assert.Equal("Standard", p.Format));
        Assert.Equal(new DateTime(2026, 10, 20, 18, 20, 5, DateTimeKind.Utc), wiki.FetchedAt);

        // Dreadsoul Corrupter has related cards (the Dreadseed tokens) but no generated pool.
        Assert.Empty(WikiPools.Parse(Fixture("EDR_841")).Pools);
    }

    [Fact]
    public void Card_ids_are_read_from_cards_or_card_ids_once_each()
    {
        using var doc = JsonDocument.Parse("""
            {"generated_card_pools":[
              {"pool":"Discovered Beasts","card_ids":["CORE_CS2_120"," CORE_CS2_120 ","EX1_543",""]},
              {"name":"Summoned minions","cards":[{"id":"CORE_EX1_506"},{"card_id":"CORE_EX1_506"}]},
              {"pool":"Empty","cards":[],"card_ids":[]}
            ]}
            """);

        var wiki = WikiPools.Parse(doc.RootElement);

        Assert.Equal(2, wiki.Pools.Count);
        Assert.Equal(["CORE_CS2_120", "EX1_543"], wiki.Pools[0].CardIds);
        Assert.Equal("Beasts", wiki.Pools[0].Label);
        Assert.Equal(["CORE_EX1_506"], wiki.Pools[1].CardIds);
        Assert.Empty(wiki.Pools[1].HeroClasses);
        Assert.Null(wiki.Pools[1].Format);
    }

    [Fact]
    public async Task The_wiki_pools_come_first_with_a_section_each_and_their_source()
    {
        var (engine, wiki) = await EngineAsync("BE_298");

        var view = engine.Build("BE_298", DeathKnight, PoolTrigger.Choice)!;

        Assert.Equal(WikiPools.Source, view.Source);
        Assert.Equal(70, view.Count);
        Assert.Equal([new PoolSection("Blood Rune", 29), new PoolSection("Frost Rune", 21), new PoolSection("Unholy Rune", 33)], view.Sections);
        // Cards of several runes are in several sections; the stats count each card once.
        Assert.Equal(70, view.Cost.Count);
        Assert.Contains(view.Cards, c => c.Sections.Count > 1);
        Assert.Equal(29, view.Cards.Count(c => c.Sections.Contains("Blood Rune")));
        Assert.Contains(view.Cards, c => c.CardId == "RLK_709" && c.Sections.Contains("Frost Rune"));
        Assert.NotNull(wiki);
    }

    [Fact]
    public async Task An_older_wiki_list_that_the_card_text_finds_whole_gives_way_to_the_text_pool()
    {
        // Hematurge's wiki page was read on 2026-10-12: its 23 Blood Rune cards miss the 6 of the newest set.
        var (engine, _) = await EngineAsync("CORE_RLK_066");

        var view = engine.Build("CORE_RLK_066", DeathKnight, PoolTrigger.Hover)!;

        Assert.Equal(29, view.Count);
        Assert.Equal("card text (the Hearthstone Wiki list is older: 23 of 29)", view.Source);
        Assert.Empty(view.Sections);
    }

    [Fact]
    public async Task Wiki_pools_of_another_class_or_another_format_are_not_used()
    {
        var (engine, _) = await EngineAsync("BE_298");

        // A Scion of Insight stolen by a Mage: the wiki queried Death Knight; the text pool (any class) answers.
        var stolen = engine.Build("BE_298", DeathKnight with { OwnClass = "MAGE" }, PoolTrigger.Choice)!;
        Assert.Equal(CardPoolEngine.TextSource, stolen.Source);
        Assert.Equal(70, stolen.Count);

        // The wiki lists are Standard queries.
        Assert.Equal(CardPoolEngine.TextSource, engine.Build("BE_298", DeathKnight with { Format = "FT_WILD" }, PoolTrigger.Choice)!.Source);
    }

    [Fact]
    public async Task The_text_pool_shows_until_the_wiki_answer_arrives_and_then_the_wiki_view()
    {
        var cache = CacheWith("BE_298");
        var wiki = new WikiPoolSource(new HearthpulseData(new HearthpulseApi(new HttpClient(new NoNetwork())), cache));
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty, wiki: wiki);
        var changed = 0;
        wiki.Changed += () => changed++;

        var before = engine.Build("BE_298", DeathKnight, PoolTrigger.Choice)!;
        await wiki.LoadAsync("BE_298");
        var after = engine.Build("BE_298", DeathKnight, PoolTrigger.Choice)!;

        Assert.Equal(CardPoolEngine.TextSource, before.Source);
        Assert.Equal(WikiPools.Source, after.Source);
        Assert.Same(after, engine.Build("BE_298", DeathKnight, PoolTrigger.Choice));
        Assert.True(changed >= 1);
    }

    [Fact]
    public async Task A_generator_without_wiki_pools_keeps_its_pool_not_computed_view()
    {
        var (engine, _) = await EngineAsync("EDR_841");

        var view = engine.Build("EDR_841", DeathKnight with { OwnClass = "DEMONHUNTER" }, PoolTrigger.Hover)!;

        Assert.Equal("a named group of cards that the catalog does not mark", view.Uncomputed);
    }

    private static async Task<(CardPoolEngine Engine, WikiPoolSource Wiki)> EngineAsync(string cardId)
    {
        var wiki = new WikiPoolSource(new HearthpulseData(new HearthpulseApi(new HttpClient(new NoNetwork())), CacheWith(cardId)));
        await wiki.LoadAsync(cardId);
        return (new CardPoolEngine(Catalog.Value, CardDb.Empty, wiki: wiki), wiki);
    }

    /// <summary>
    /// An API cache folder holding the fixture answer for the card, as HearthpulseData writes it, written <paramref name="age"/>
    /// ago. A copy keeps the fixture's own write time (the checkout's), which would make the answer stale after 6 hours.
    /// </summary>
    private static string CacheWith(string cardId, TimeSpan age = default)
    {
        var folder = Path.Combine(TestDataFolder.Path, "wiki-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"card-wiki-{cardId}.json");
        File.Copy(FixturePath(cardId), file);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        return folder;
    }

    private static string FixturePath(string cardId) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "CardWiki", cardId + ".json");

    private static JsonElement Fixture(string cardId) =>
        JsonDocument.Parse(File.ReadAllText(FixturePath(cardId))).RootElement.GetProperty("data");

    [Fact]
    public async Task A_stale_answer_is_asked_for_again_and_read_from_the_cache_when_the_server_fails()
    {
        var server = new Failing();
        var wiki = new WikiPoolSource(new HearthpulseData(new HearthpulseApi(new HttpClient(server)), CacheWith("BE_298", TimeSpan.FromDays(2))));

        var answer = await wiki.LoadAsync("BE_298");

        Assert.Equal(1, server.Requests);
        Assert.Equal([29, 21, 33], answer.Pools.Select(p => p.CardIds.Count));
    }

    /// <summary>The API answering 503: HearthpulseData falls back to its stale cache.</summary>
    private sealed class Failing : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"detail\":\"maintenance\"}") });
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A test reached the network: {request.RequestUri}");
    }
}
