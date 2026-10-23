using System.Text;
using Hearthlens.Core.Cards;
using Xunit;

namespace Hearthlens.Tests;

/// <summary>The hsbg.cards supplement (second data source) parsing and merge rules.</summary>
public sealed class BgCardsTests
{
    private const string Single = """
        {"data":{"id":64038,"slug":"sellemental","name":"Sellemental","image":"/cards/sellemental.webp","imageGold":"/cards/sellemental_g.webp","text":"When you sell this, get a 3/3 Elemental.","attack":3,"health":3,"manaCost":0,"tier":2,"cardType":"minion","minionType":"Elemental","minionTypes":["Elemental"],"keywords":[],"keywordIds":[],"isHero":false,"isHeroSkin":false,"isQuest":false,"isReward":false,"isDuosOnly":false,"isTimewarped":false,"childIds":[],"parentId":120800,"pool":true,"categories":["tavern"],"externalId":"BGS_115","dbfIdGold":64041}}
        """;

    [Fact]
    public void Parses_single_card_response()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Single));
        var db = BgCardDb.Parse(stream);

        var card = db.Get("BGS_115");
        Assert.NotNull(card);
        Assert.Equal(64038, card!.Id);
        Assert.Equal(2, card.TechLevel);
        Assert.True(card.IsMinion);
        Assert.True(card.IsPoolCard);
        Assert.Equal(64041, card.GoldenDbfId);
        Assert.Equal(120800, card.ParentId);
        Assert.Equal("https://hsbg.cards/cards/sellemental.webp", db.ImageUrl("BGS_115"));
        Assert.Equal("https://hsbg.cards/cards/sellemental_g.webp", db.ImageUrl("BGS_115", golden: true));
    }

    [Fact]
    public void Bulk_page_keeps_valid_cards_and_marks_tokens_out_of_the_pool()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"data":[
                {"id":1,"externalId":"","name":"No id","tier":1,"cardType":"minion","categories":["tavern"]},
                {"id":2,"externalId":"TOKEN_1","name":"Token","tier":1,"cardType":"minion","categories":["token"]},
                {"id":3,"externalId":"SPELL_1","name":"A Spell","tier":2,"cardType":"spell","categories":["tavern"]},
                "junk"
            ],"pagination":{"total":4,"limit":100,"offset":0,"nextOffset":4}}
            """));
        var db = BgCardDb.Parse(stream);

        Assert.Equal(2, db.Count);
        Assert.True(db.Get("SPELL_1")!.IsSpell);
        Assert.True(db.Get("SPELL_1")!.IsPoolCard);
        Assert.False(db.Get("TOKEN_1")!.IsPoolCard);
    }

    [Fact]
    public void Supplement_fills_missing_fields_on_known_cards()
    {
        var cards = ParseCards("""
            [ { "id": "BGS_115", "dbfId": 64038, "name": "Sellemental", "type": "MINION" } ]
            """);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Single));
        var db = BgCardDb.Parse(stream);

        cards = CardDb.ApplySupplement(cards, db);

        var card = Assert.Single(cards);
        Assert.Equal(2, card.TechLevel);
        Assert.True(card.IsBattlegroundsPoolMinion);
        Assert.Equal("ELEMENTAL", Assert.Single(card.Races));
        Assert.Equal(64041, card.BattlegroundsPremiumDbfId);
    }

    [Fact]
    public void Supplement_adds_current_patch_pool_cards_missing_from_hearthstonejson()
    {
        var cards = ParseCards("""
            [ { "id": "OLD_CARD", "dbfId": 1, "name": "Old", "type": "MINION", "techLevel": 1 } ]
            """);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"data":[
                {"id":70000,"externalId":"NEW_MINION","name":"Brand New","text":"Shiny.","manaCost":0,"attack":4,"health":4,"tier":3,"cardType":"minion","minionTypes":["Murloc","Beast"],"keywords":["Divine Shield"],"tierX":0,"pool":true,"categories":["tavern"],"dbfIdGold":70001,"isTimewarped":false},
                {"id":70002,"externalId":"NEW_TOKEN","name":"Brand Token","tier":3,"cardType":"minion","pool":true,"categories":["token"]},
                {"id":70003,"externalId":"TIMEWARPED","name":"Warped","tier":3,"cardType":"minion","pool":true,"categories":["tavern"],"isTimewarped":true}
            ]}
            """));
        var db = BgCardDb.Parse(stream);

        cards = CardDb.ApplySupplement(cards, db);

        Assert.Equal(2, cards.Count);
        var card = Assert.Single(cards, c => c.Id == "NEW_MINION");
        Assert.True(card.IsBattlegroundsPoolMinion);
        Assert.Equal(3, card.TechLevel);
        Assert.Equal(70001, card.BattlegroundsPremiumDbfId);
        Assert.Contains("DIVINE_SHIELD", card.Mechanics);
        Assert.Contains("MURLOC", card.Races);
        Assert.DoesNotContain(cards, c => c.Id is "NEW_TOKEN" or "TIMEWARPED");
    }

    private static List<Card> ParseCards(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return CardDb.ParseList(stream);
    }
}
