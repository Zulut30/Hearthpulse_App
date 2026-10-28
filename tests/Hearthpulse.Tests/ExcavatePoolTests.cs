using System.IO.Compression;
using System.Net;
using System.Text;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Tests;

/// <summary>
/// The Excavate pool: every card that Excavates (or counts Excavates) and every treasure shows the treasures by tier,
/// the next tier from the player entity's Power.log tags. The catalog is the card pool fixture; nothing reaches the network.
/// </summary>
public sealed class ExcavatePoolTests
{
    private static readonly Lazy<CardPoolCatalog> Catalog = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return CardPoolCatalog.Read(gz)!;
    });

    private static readonly CardDb Heroes = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_11","dbfId":78065,"name":"The Lich King","type":"HERO","cardClass":"DEATHKNIGHT"},
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"HERO_01","dbfId":7,"name":"Garrosh","type":"HERO","cardClass":"WARRIOR"}
        ]
        """u8.ToArray()));

    private static PoolContext Wild(string ownClass, ExcavateState? state = null) => new("FT_WILD", ownClass, "WARRIOR") { Excavate = state };

    [Fact]
    public void Every_catalog_card_with_Excavate_gets_the_treasure_pool()
    {
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty);
        var cards = Catalog.Value.Cards
            .Where(c => c.Mechanics.Contains(Excavate.Mechanic) || c.ReferencedTags.Contains(Excavate.Mechanic))
            .ToList();

        // 17 cards Excavate (Showdown in the Badlands and Delve into Deepholm); 7 more count Excavates.
        Assert.Equal(17, cards.Count(c => c.Mechanics.Contains(Excavate.Mechanic)));
        Assert.True(cards.Count >= 24, $"{cards.Count} Excavate cards in the fixture");
        foreach (var card in cards)
        {
            var ownClass = card.Classes.FirstOrDefault(c => c != "NEUTRAL") ?? "DEATHKNIGHT";
            var view = engine.Build(card.Id, Wild(ownClass, new ExcavateState(1, Excavate.TierCount(ownClass))), PoolTrigger.Hover);
            Assert.True(view is not null, $"{card.Id} {card.Name} shows no pool");
            if (view!.Excavate is null)
            {
                // Only a card that counts Excavates and has a pool of its own ("cast two random Mage Secrets") keeps it.
                Assert.DoesNotContain(Excavate.Mechanic, card.Mechanics);
                Assert.Equal(CardPoolEngine.TextSource, view.Source);
                continue;
            }

            Assert.Equal(Excavate.Source, view.Source);
            Assert.All(view.Cards, c => Assert.NotNull(Excavate.TreasureOf(c.CardId)));
        }
    }

    [Fact]
    public void Treasures_show_the_Excavate_pool_unless_they_have_a_pool_of_their_own()
    {
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty);

        // Rock (Deal 3 damage) has no pool: its panel is the treasures.
        Assert.NotNull(engine.Build("WW_001t", Wild("MAGE"), PoolTrigger.Hover)?.Excavate);
        // Azerite Gem (Discover a 5-Cost card) keeps the pool of its text.
        var gem = engine.Build("WW_001t14", Wild("MAGE"), PoolTrigger.Hover);
        Assert.NotNull(gem);
        Assert.Null(gem!.Excavate);
        Assert.All(gem.Cards, c => Assert.Equal(5, c.Cost));
        // A card with no Excavate at all shows nothing new.
        Assert.False(engine.Excavates("CORE_CS2_029"));
    }

    [Fact]
    public void The_next_tier_comes_first_and_tier_4_is_the_class_legendary()
    {
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty);

        var knight = engine.Build("WW_001", Wild("DEATHKNIGHT", new ExcavateState(4, 4)), PoolTrigger.Hover)!.Excavate!;
        Assert.Equal(4, knight.Next);
        Assert.Equal([4, 1, 2, 3], knight.FromNext.Select(t => t.Tier));
        Assert.Equal(["WW_001t26"], knight.Tiers[3].Treasures.Select(t => t.CardId));
        Assert.Equal("LEGENDARY", knight.Tiers[3].Rarity);
        Assert.All(knight.Tiers.Take(3), t => Assert.Equal(6, t.Treasures.Count));

        // A class without a legendary treasure goes back to common after the epic.
        var priest = engine.Build("WW_001", Wild("PRIEST", new ExcavateState(3, 3)), PoolTrigger.Hover)!.Excavate!;
        Assert.Equal([3, 1, 2], priest.FromNext.Select(t => t.Tier));
        Assert.Equal(18, priest.Tiers.Sum(t => t.Treasures.Count));

        // Outside a game (a deck row): every tier, no next one, all seven legendaries when the class is not known.
        var browse = engine.Build("WW_001", new PoolContext("FT_WILD", null, null), PoolTrigger.Request)!.Excavate!;
        Assert.Null(browse.Next);
        Assert.Equal(7, browse.Tiers[3].Treasures.Count);
    }

    [Fact]
    public void Other_pools_stay_the_same_object_when_the_tier_moves()
    {
        var engine = new CardPoolEngine(Catalog.Value, CardDb.Empty);
        var before = engine.Build("CORE_GIL_836", Wild("SHAMAN", new ExcavateState(1, 4)), PoolTrigger.Hover);
        var after = engine.Build("CORE_GIL_836", Wild("SHAMAN", new ExcavateState(2, 4)), PoolTrigger.Hover);
        Assert.NotNull(before);
        Assert.Same(before, after);

        var first = engine.Build("WW_001", Wild("SHAMAN", new ExcavateState(1, 4)), PoolTrigger.Hover)!;
        var second = engine.Build("WW_001", Wild("SHAMAN", new ExcavateState(2, 4)), PoolTrigger.Hover)!;
        Assert.Equal(1, first.Excavate!.Next);
        Assert.Equal(2, second.Excavate!.Next);
        Assert.Same(second, engine.Build("WW_001", Wild("SHAMAN", new ExcavateState(2, 4)), PoolTrigger.Hover));
    }

    [Fact]
    public void The_tier_comes_from_the_player_entity_tags()
    {
        // MAX_EXCAVATE_TIER is 0-based: 3 for a Death Knight (four tiers), as in the client's logs.
        var parser = Game("HERO_11", maxTier: 3);
        var game = parser.Game!;
        Assert.Equal(new ExcavateState(1, 4), CardPoolTracker.ContextOf(game, Heroes).Excavate);

        Feed(parser, ["DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_EXCAVATE_TIER value=3 "]);
        Assert.Equal(new ExcavateState(4, 4), CardPoolTracker.ContextOf(game, Heroes).Excavate);

        // A Priest has three tiers; past the end the count starts over.
        var priest = Game("HERO_09", maxTier: 2);
        Feed(priest, ["DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=CURRENT_EXCAVATE_TIER value=3 "]);
        Assert.Equal(new ExcavateState(1, 3), CardPoolTracker.ContextOf(priest.Game!, Heroes).Excavate);
    }

    [Fact]
    public void Without_the_tier_tag_the_treasures_the_player_got_count()
    {
        var parser = Game("HERO_11", maxTier: null);
        Feed(parser, [
            "DebugPrintPower() - FULL_ENTITY - Creating ID=60 CardID=WW_001t4",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=MINION",
            // The opponent's treasure is not the player's cycle.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=61 CardID=WW_001t5",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ]);

        // No MAX_EXCAVATE_TIER either: the class decides (Death Knight, four tiers).
        Assert.Equal(new ExcavateState(2, 4), CardPoolTracker.ContextOf(parser.Game!, Heroes).Excavate);
    }

    [Fact]
    public async Task Paging_stops_when_a_page_past_the_end_repeats_a_full_last_page()
    {
        // 400 cards: two full pages, then the server repeats page 2 for every later page.
        var server = new Pages(400);
        var cards = await CardPoolCatalog.FetchApiAsync(new HearthpulseApi(new HttpClient(server)), CancellationToken.None);

        Assert.Equal(400, cards!.Count);
        Assert.Equal(3, server.Requests);
    }

    private sealed class Pages(int total) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var size = int.Parse(query["per_page"]!, System.Globalization.CultureInfo.InvariantCulture);
            var last = Math.Max(1, (total + size - 1) / size);
            var page = Math.Min(int.Parse(query["page"]!, System.Globalization.CultureInfo.InvariantCulture), last);
            var items = Enumerable.Range((page - 1) * size, Math.Min(size, total - (page - 1) * size))
                .Select(i => $$"""{"card_id":"C_{{i}}","dbf":{{i + 1}},"name":{"en":"Card {{i}}"},"card_type":{"slug":"SPELL"},"mana_cost":1}""");
            var body = "{\"data\":[" + string.Join(',', items) + "]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static PowerParser Game(string hero, int? maxTier)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 28));
        var lines = new List<string>
        {
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=5",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
        };
        if (maxTier is { } max)
        {
            lines.Add($"DebugPrintPower() -         tag=MAX_EXCAVATE_TIER value={max}");
        }

        lines.AddRange([
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=21",
            "DebugPrintPower() -         tag=MAX_EXCAVATE_TIER value=2",
            $"DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID={hero}",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=21 CardID=HERO_01",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_WILD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            // Kobold Miner in the opening hand: a revealed hand card tells which player is local.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=50 CardID=WW_001",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=MINION",
        ]);
        Feed(parser, lines);
        return parser;
    }

    private static void Feed(PowerParser parser, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
    }
}
