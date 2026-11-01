using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

/// <summary>Deck codes built from a card list (decks brought over from HDT, which keeps no code).</summary>
public sealed class DeckStringEncodeTests
{
    [Fact]
    public void A_list_encodes_and_decodes_back()
    {
        var deck = new Dictionary<string, int> { ["CS2_029"] = 2, ["EX1_279"] = 1, ["CS2_032"] = 3 };

        var code = DeckString.Encode(deck, "MAGE", "Standard", HdtImportTests.Cards)!;
        var back = DeckString.TryDecode(code)!;

        Assert.Equal("Standard", back.Format);
        Assert.Equal([637], back.HeroDbfIds);
        Assert.Equal(new Dictionary<int, int> { [315] = 2, [1087] = 1, [1004] = 3 }, back.Cards);
        Assert.Equal("Wild", DeckString.TryDecode(DeckString.Encode(deck, "MAGE", "Wild", HdtImportTests.Cards)!)!.Format);
    }

    [Fact]
    public void A_list_with_an_unknown_card_or_class_has_no_code()
    {
        Assert.Null(DeckString.Encode(new Dictionary<string, int> { ["NOPE_001"] = 2 }, "MAGE", "Standard", HdtImportTests.Cards));
        Assert.Null(DeckString.Encode(new Dictionary<string, int> { ["CS2_029"] = 2 }, "WIZARD", "Standard", HdtImportTests.Cards));
        Assert.Null(DeckString.Encode(new Dictionary<string, int>(), "MAGE", "Standard", HdtImportTests.Cards));
        Assert.Equal("HERO_10", DeckString.ClassHero("DemonHunter"));
    }
}
