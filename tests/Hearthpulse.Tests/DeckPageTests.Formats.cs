using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>The Standard and Wild pages: one Constructed page shown for one format.</summary>
public sealed partial class DeckPageTests
{
    [Fact]
    public void The_Standard_and_Wild_pages_show_only_their_formats_decks_suggestions_and_games()
    {
        var games = new List<GameRecord>
        {
            Game("s1", "DK 1", Code(Deck(1000)), "Win", 0, 11),
            Game("s2", "DK 2", Code(Deck(1000, 1014)), "Loss", 10, 12),
            Game("w1", "Wild DK 1", Code(Deck(2000)), "Win", 20, 21, format: "FT_WILD"),
            Game("w2", "Wild DK 2", Code(Deck(2000, 2014)), "Win", 30, 22, format: "FT_WILD"),
            Game("t1", "Twist", Code(Deck(3000)), "Win", 40, 31, format: "FT_TWIST"),
        };
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);
        var suggestions = DeckFamilies.Suggest(families, new HashSet<(string, string)>());
        Assert.Equal(2, suggestions.Count);

        var standard = ConstructedPage.FormatOf(ConstructedPage.Standard);
        var wild = ConstructedPage.FormatOf(ConstructedPage.Wild);
        Assert.Equal(["DK 2", "DK 1"], ConstructedPage.Decks(families, standard).Select(f => f.Name));
        Assert.Equal(["Wild DK 2", "Wild DK 1"], ConstructedPage.Decks(families, wild).Select(f => f.Name));
        Assert.All(ConstructedPage.Suggestions(suggestions, standard), s => Assert.Equal("FT_STANDARD", s.Second.Format));
        Assert.Equal("FT_WILD", Assert.Single(ConstructedPage.Suggestions(suggestions, wild)).Second.Format);

        // My stats count the page's format only.
        Assert.Equal(["s1", "s2"], games.Where(g => StatsFilter.IsConstructed(g, standard)).Select(g => g.Key));
        Assert.Equal(["w1", "w2"], games.Where(g => StatsFilter.IsConstructed(g, wild)).Select(g => g.Key));
    }

    [Theory]
    [InlineData("Constructed", "Standard")]
    [InlineData("Standard", "Standard")]
    [InlineData("Wild", "Wild")]
    [InlineData("History", "History")]
    [InlineData(null, "")]
    public void The_old_Constructed_page_opens_as_Standard(string? page, string expected) =>
        Assert.Equal(expected, ConstructedPage.Migrate(page));

    [Theory]
    [InlineData("FT_WILD", "Wild", "FT_WILD")]
    [InlineData("FT_STANDARD", "Standard", "FT_STANDARD")]
    [InlineData("FT_TWIST", "Standard", "FT_STANDARD")]
    [InlineData(null, "Standard", "FT_STANDARD")]
    public void A_deck_opens_on_its_formats_page(string? format, string page, string pageFormat)
    {
        Assert.Equal(page, ConstructedPage.PageOf(format));
        Assert.Equal(pageFormat, ConstructedPage.FormatOf(page));
    }
}
