using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The hero behind each main-window page (V3 direction A): picked from finished games and decks.</summary>
public sealed partial class DeckPageTests
{
    private static string ClassHero(string playerClass) => "HERO_" + playerClass;

    private static GameRecord BgGame(string key, string hero, int place) =>
        new() { Key = key, Mode = "Battlegrounds", HeroCardId = hero, Placement = place, Result = place <= 4 ? "Win" : "Loss" };

    [Fact]
    public void Battlegrounds_shows_the_hero_with_the_best_average_place()
    {
        GameRecord[] games =
        [
            BgGame("1", "BG_A", 2), BgGame("2", "BG_A", 3), BgGame("3", "BG_A", 1),
            BgGame("4", "BG_B", 1), BgGame("5", "BG_B", 1), BgGame("6", "BG_B", 3),
            // One first place is not enough games to count as the best
            BgGame("7", "BG_C", 1),
        ];

        Assert.Equal("BG_B", PageHeroes.For("Battlegrounds", "LAST", games, [], null, ClassHero));
    }

    [Fact]
    public void Battlegrounds_without_enough_games_shows_the_hero_played_most_then_Bob()
    {
        GameRecord[] few = [BgGame("1", "BG_A", 1), BgGame("2", "BG_B", 5), BgGame("3", "BG_B", 6)];

        Assert.Equal("BG_B", PageHeroes.For("Battlegrounds", "LAST", few, [], null, ClassHero));
        Assert.Equal(PageHeroes.Bob, PageHeroes.For("Battlegrounds", "LAST", [], [], null, ClassHero));
    }

    [Fact]
    public void Standard_and_Wild_show_the_class_of_their_deck_played_most()
    {
        var games = new[]
        {
            Game("a", "Mage", Code(Deck(1000)), "Win", 0, 1, playerClass: "MAGE"),
            Game("b", "Priest", Code(Deck(2000)), "Win", 10, 2, playerClass: "PRIEST"),
            Game("c", "Priest", Code(Deck(2000)), "Loss", 20, 2, playerClass: "PRIEST"),
            Game("d", "Rogue", Code(Deck(3000)), "Win", 30, 3, format: "FT_WILD", playerClass: "ROGUE"),
        };
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);

        Assert.Equal("HERO_PRIEST", PageHeroes.For(ConstructedPage.Standard, "LAST", games, families, null, ClassHero));
        Assert.Equal("HERO_ROGUE", PageHeroes.For(ConstructedPage.Wild, "LAST", games, families, null, ClassHero));
    }

    [Fact]
    public void A_page_with_nothing_played_falls_back_to_the_last_hero_then_the_innkeeper()
    {
        Assert.Equal("LAST", PageHeroes.For(ConstructedPage.Wild, "LAST", [], [], null, ClassHero));
        Assert.Equal("LAST", PageHeroes.For("Arena", "LAST", [], [], null, ClassHero));
        Assert.Equal(PageHeroes.Innkeeper, PageHeroes.For("Overview", null, [], [], null, ClassHero));
        Assert.Equal(PageHeroes.Innkeeper, PageHeroes.For("Settings", "LAST", [], [], null, ClassHero));
    }

    [Fact]
    public void Arena_shows_the_last_run_s_class_and_History_the_open_game_s_hero()
    {
        GameRecord[] games =
        [
            new() { Key = "2", Mode = "Arena", PlayerClass = "HUNTER", HeroCardId = "HERO_05" },
            new() { Key = "1", Mode = "Arena", PlayerClass = "WARLOCK", HeroCardId = "HERO_07" },
        ];

        Assert.Equal("HERO_HUNTER", PageHeroes.For("Arena", "LAST", games, [], null, ClassHero));
        Assert.Equal("OPEN", PageHeroes.For("History", "LAST", games, [], "OPEN", ClassHero));
        Assert.Equal("LAST", PageHeroes.For("History", "LAST", games, [], null, ClassHero));
    }
}
