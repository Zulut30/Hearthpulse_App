using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Tests.EctoplasmListTests;

namespace Hearthpulse.Tests;

/// <summary>
/// A card in hand's related cards (the tokens it makes, the cards it names) and the Graveyard panel's dead minions;
/// synthetic card data and catalog, synthetic Power.log lines.
/// </summary>
public sealed class RelatedCardsTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"CS2_101","dbfId":1,"name":"Reinforce","type":"HERO_POWER","set":"CORE","collectible":false},
          {"id":"GEN_1","dbfId":10,"name":"Muster for Battle","type":"SPELL","cost":3,"set":"TGT","collectible":true},
          {"id":"GEN_1t","dbfId":11,"name":"Silver Hand Recruit","type":"MINION","cost":1,"set":"TGT"},
          {"id":"GEN_1t2","dbfId":12,"name":"Light's Justice","type":"WEAPON","cost":1,"set":"TGT"},
          {"id":"GEN_1e","dbfId":13,"name":"Muster buff","type":"ENCHANTMENT","set":"TGT"},
          {"id":"GEN_1t3","dbfId":14,"name":"Other set token","type":"MINION","cost":1,"set":"OTHER"},
          {"id":"CORE_GEN_1","dbfId":15,"name":"Muster for Battle","type":"SPELL","cost":3,"set":"CORE","collectible":true},
          {"id":"SHUF_1","dbfId":20,"name":"Shuffler","type":"MINION","cost":2,"set":"X","collectible":true},
          {"id":"FIRE_1","dbfId":21,"name":"Fireball","type":"SPELL","cost":4,"set":"X","collectible":true},
          {"id":"CHARGE_1","dbfId":22,"name":"Charge","type":"SPELL","cost":1,"set":"X","collectible":true},
          {"id":"PLAIN","dbfId":30,"name":"Chillwind Yeti","type":"MINION","cost":4,"set":"X","collectible":true}
        ]
        """u8.ToArray()));

    private static PoolCard Pool(string id, string name, string text) =>
        new(id, 0, name, text, "SPELL", "COMMON", 1, null, null, [], [], null, [], [], "X", true);

    private static readonly CardPoolCatalog Catalog = new(
    [
        Pool("SHUF_1", "Shuffler", "<b>Battlecry:</b> Shuffle a Fireball into your deck. <b>Charge</b>"),
        Pool("FIRE_1", "Fireball", "Deal $6 damage."),
        Pool("CHARGE_1", "Charge", "Give a minion <b>Charge</b>."),
        Pool("PLAIN", "Chillwind Yeti", "A Fireballer's tale."),
        Pool("GEN_1", "Muster for Battle", "Summon three 1/1 Silver Hand Recruits. Equip a 1/4 weapon."),
    ], "test", DateTime.UtcNow);

    [Fact]
    public void A_cards_tokens_by_id_are_its_related_cards_also_for_its_core_reprint()
    {
        var list = RelatedCards.Of("GEN_1", Cards, Catalog)!;

        Assert.Equal(Loc.T("Related.Title"), list.Title);
        Assert.Equal(["GEN_1t", "GEN_1t2"], list.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
        Assert.All(list.Rows, r => Assert.Equal(1, r.Count));
        Assert.Equal(["GEN_1t", "GEN_1t2"], RelatedCards.Of("CORE_GEN_1", Cards, Catalog)!.Rows.Select(r => r.CardId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_card_named_in_the_text_is_related_but_not_a_bold_keyword_or_part_of_a_word()
    {
        var list = RelatedCards.Of("SHUF_1", Cards, Catalog)!;

        Assert.Equal(["FIRE_1"], list.Rows.Select(r => r.CardId));
        Assert.Null(RelatedCards.Of("PLAIN", Cards, Catalog));
        Assert.Null(RelatedCards.Of("CHARGE_1", Cards, Catalog));
        Assert.Null(RelatedCards.Of("NOPE", Cards, Catalog));
    }

    [Fact]
    public void The_graveyard_lists_each_sides_dead_minions_and_nothing_else()
    {
        var (before, beforeTracker) = Play([[.. Minion(40, "PLAIN", 1)]]);
        Assert.Null(GraveyardView.Of(before, beforeTracker.Counters, Cards));

        var (game, tracker) = Play(
        [
            [.. Minion(40, "PLAIN", 1), .. Minion(41, "PLAIN", 1), .. Minion(42, "SHUF_1", 1), .. Minion(50, "SHUF_1", 2)],
            // A card in the opponent's hand that goes to the graveyard (discarded) is no death on the board.
            [.. Card(60, "PLAIN", "HAND", 2, "MINION")],
            [.. Die(40, "PLAIN", 1), .. Die(41, "PLAIN", 1), .. Die(50, "SHUF_1", 2)],
            ["DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=60 zone=HAND zonePos=1 cardId=PLAIN player=2] tag=ZONE value=GRAVEYARD "],
        ]);

        var view = GraveyardView.Of(game, tracker.Counters, Cards)!;

        Assert.Equal(2, view.LocalTotal);
        Assert.Equal([("PLAIN", 2)], view.Local.Select(r => (r.CardId, r.Count)));
        Assert.Equal(1, view.OpponentTotal);
        Assert.Equal([("SHUF_1", 1)], view.Opponent.Select(r => (r.CardId, r.Count)));
    }
}
