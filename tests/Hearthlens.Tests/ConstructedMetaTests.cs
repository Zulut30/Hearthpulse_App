using System.Text.Json;
using Hearthlens.Core.Constructed;
using Xunit;

namespace Hearthlens.Tests;

public sealed class ConstructedMetaTests
{
    private const string Archetypes = """
        {
          "lastUpdated": "2026-10-22T00:59:58.000Z",
          "archetypeStats": [
            {
              "id": 815, "name": "quest-priest", "heroCardClass": "priest", "totalGames": 1000, "totalWins": 580,
              "coreCards": ["A", "B", "C", "D", "E"],
              "coinPlayInfo": [
                { "coinPlay": "play", "totalGames": 500, "wins": 300,
                  "cardsData": [ { "cardId": "A", "drawnBeforeMulligan": 100, "keptInMulligan": 90, "inHandAfterMulligan": 95, "inHandAfterMulliganThenWin": 70 } ] },
                { "coinPlay": "coin", "totalGames": 500, "wins": 280,
                  "cardsData": [ { "cardId": "A", "drawnBeforeMulligan": 100, "keptInMulligan": 80, "inHandAfterMulligan": 85, "inHandAfterMulliganThenWin": 56 },
                                 { "cardId": "Z", "drawnBeforeMulligan": 5, "keptInMulligan": 1, "inHandAfterMulligan": 3, "inHandAfterMulliganThenWin": 3 } ] }
              ]
            },
            { "id": 632, "name": "warlock", "heroCardClass": "warlock", "totalGames": 400, "totalWins": 180, "coreCards": [], "coinPlayInfo": [] },
            { "id": 9, "name": "tiny-mage", "heroCardClass": "mage", "totalGames": 20, "totalWins": 15, "coreCards": [], "coinPlayInfo": [] }
          ]
        }
        """;

    private const string Decks = """
        {
          "deckStats": [
            { "decklist": "AAEC", "archetypeId": 815, "archetypeName": "quest-priest", "playerClass": "priest", "totalGames": 300, "totalWins": 180 },
            { "decklist": "AAED", "archetypeId": 815, "archetypeName": "quest-priest", "playerClass": "priest", "totalGames": 10, "totalWins": 9 }
          ]
        }
        """;

    private static ConstructedMeta Parse()
    {
        using var archetypes = JsonDocument.Parse(Archetypes);
        using var decks = JsonDocument.Parse(Decks);
        return ConstructedMeta.Parse("standard", "all", "past-7", archetypes.RootElement.Clone(), decks.RootElement.Clone());
    }

    [Fact]
    public void Archetypes_have_win_rate_share_and_coin_split()
    {
        var meta = Parse();

        var quest = Assert.Single(meta.Archetypes, a => a.Id == 815);
        Assert.Equal("Quest Priest", quest.Name);
        Assert.Equal(0.58, quest.WinRate, 3);
        Assert.Equal(0.6, quest.FirstWinRate, 3);
        Assert.Equal(0.56, quest.CoinWinRate, 3);
        Assert.Equal(1000 / 1420.0, quest.Share, 3);
        Assert.Equal("Other Warlock", meta.Archetypes.Single(a => a.Id == 632).Name);

        // Too few games to rank.
        Assert.DoesNotContain(meta.Archetypes, a => a.Id == 9);
    }

    [Fact]
    public void Mulligan_adds_coin_and_play_and_drops_rare_cards()
    {
        var card = Assert.Single(Parse().Archetypes.Single(a => a.Id == 815).Mulligan);

        Assert.Equal("A", card.CardId);
        Assert.Equal(180, card.InHand);
        Assert.Equal(126 / 180.0, card.InHandWinRate, 3);
        Assert.Equal(0.85, card.KeepRate, 3);
    }

    [Fact]
    public void Decks_need_enough_games_and_take_the_archetype_name()
    {
        var deck = Assert.Single(Parse().Decks);

        Assert.Equal("AAEC", deck.DeckString);
        Assert.Equal("Quest Priest", deck.Archetype);
        Assert.Equal(0.6, deck.WinRate, 3);
    }

    [Fact]
    public void A_deck_matches_the_archetype_whose_core_it_holds()
    {
        var meta = Parse();

        Assert.Equal(815, meta.Match("PRIEST", ["A", "B", "C", "X"])?.Id);
        Assert.Null(meta.Match("PRIEST", ["A", "X", "Y"]));
        Assert.Null(meta.Match("MAGE", ["A", "B", "C", "D"]));
    }

    [Theory]
    [InlineData("xl-rafaamlock", "warlock", "XL Rafaamlock")]
    [InlineData("bbu-harold-dk", "deathknight", "BBU Harold DK")]
    [InlineData("cthun-warlock", "warlock", "C'Thun Warlock")]
    [InlineData("demonhunter", "demonhunter", "Other Demon Hunter")]
    public void Archetype_keys_read_as_names(string key, string heroClass, string expected) =>
        Assert.Equal(expected, ConstructedMeta.Prettify(key, heroClass));

    [Fact]
    public void Unknown_filters_fall_back_to_the_first_option()
    {
        Assert.Equal("standard", ConstructedMeta.Key(ConstructedMeta.Formats, "../../x"));
        Assert.Equal("legend", ConstructedMeta.Key(ConstructedMeta.Ranks, "legend"));
    }
}