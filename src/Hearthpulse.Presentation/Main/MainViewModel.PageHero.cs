using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The hero behind each main-window page (V3 direction A, <c>docs/design-identity.md</c>), picked from finished games
/// only: its art comes from the HearthstoneJSON cache, the installer carries none.
/// </summary>
public static class PageHeroes
{
    /// <summary>Calm neutral art for Settings and for a page with nothing played yet: Harth Stonebrew, the innkeeper.</summary>
    public const string Innkeeper = "GIFT_01";

    /// <summary>Bob, the Battlegrounds page's art before a game there.</summary>
    public const string Bob = "TB_BaconShopBob";

    /// <summary>Games a Battlegrounds hero needs before its average place makes it the best one.</summary>
    public const int BestHeroGames = 3;

    /// <summary>
    /// The art for <paramref name="page"/> (a <see cref="MainViewModel.NavPages"/> name): home the last game's hero,
    /// Battlegrounds the best hero, Standard and Wild the class of the deck played most there, Arena and Brawl the last
    /// game's hero in the mode, History the open game's hero, Settings the innkeeper. A page with nothing played falls
    /// back to the last game's hero, then to the innkeeper (Bob on Battlegrounds).
    /// </summary>
    /// <param name="lastHero">The hero of the last game, of any mode.</param>
    /// <param name="games">Finished games, newest first.</param>
    public static string For(string page, string? lastHero, IReadOnlyList<GameRecord> games, IEnumerable<DeckFamily> families,
                             string? openMatchHero, Func<string, string> classHero)
    {
        var last = Hero(lastHero);
        var hero = page switch
        {
            "Overview" => last,
            "Battlegrounds" => BestBattlegroundsHero(games) ?? Bob,
            ConstructedPage.Standard or ConstructedPage.Wild => ConstructedPage.Decks(families, ConstructedPage.FormatOf(page))
                .Where(f => f.Record.Games > 0 && f.PlayerClass.Length > 0)
                .MaxBy(f => f.Record.Games) is { } deck ? classHero(deck.PlayerClass) : null,
            "Arena" => games.FirstOrDefault(StatsFilter.IsArena) is { PlayerClass.Length: > 0 } run ? classHero(run.PlayerClass) : null,
            "Brawl" => Hero(games.FirstOrDefault(g => g.Mode == "Brawl")?.HeroCardId),
            "History" => Hero(openMatchHero),
            "Settings" => Innkeeper,
            _ => null,
        };
        return Hero(hero) ?? last ?? Innkeeper;
    }

    /// <summary>The Battlegrounds hero with the best average place over <see cref="BestHeroGames"/> games or more, else the one played most.</summary>
    private static string? BestBattlegroundsHero(IReadOnlyList<GameRecord> games)
    {
        var heroes = games.Where(g => g.Mode is "Battlegrounds" or "Duos" && g.Placement > 0 && g.HeroCardId.Length > 0)
            .GroupBy(g => g.HeroCardId, StringComparer.Ordinal)
            .ToList();
        return (heroes.Where(h => h.Count() >= BestHeroGames).MinBy(h => h.Average(g => g.Placement))
                ?? heroes.MaxBy(h => h.Count()))?.Key;
    }

    private static string? Hero(string? cardId) => string.IsNullOrEmpty(cardId) ? null : cardId;
}

public sealed partial class MainViewModel
{
    private string _shownPage = NavPages[0];
    private string? _openMatchHero;

    /// <summary>
    /// The art behind the page on screen, or null while the page background is off: the hero the player chose for every
    /// page, else Settings shows the theme's hero and the other pages pick by page and mode.
    /// </summary>
    public string? PageHero => !Settings.PageHeroes ? null
        : Settings.BackgroundHero.Length > 0 ? Settings.BackgroundHero
        : _shownPage == "Settings" ? CurrentTheme.Hero
        : PageHeroes.For(_shownPage, Recent.FirstOrDefault(g => g.HeroCardId.Length > 0)?.HeroCardId, _allGames.Count > 0 ? _allGames : _historyGames,
            _deckFamilies, MatchOpen ? _openMatchHero : null, ClassHero);

    /// <summary>The games, decks, open match or page behind <see cref="PageHero"/> changed.</summary>
    private void RaisePageHero() => Raise(nameof(PageHero));
}
