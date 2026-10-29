using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Navigation shared by both main windows: the sidebar pages and the page tabs by name, what showing a page does,
/// Ctrl+1…7 pages, Esc / Back to leave a detail page. The windows keep their buttons in the lists' order.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The sidebar pages in order, as the window's <c>ShowPage</c> takes them; Ctrl+1 opens the first.</summary>
    public static IReadOnlyList<string> NavPages { get; } =
        ["Overview", "Battlegrounds", ConstructedPage.Standard, ConstructedPage.Wild, "Arena", "History", "Settings"];

    /// <summary>The Battlegrounds page's tabs in order, as the window's <c>BattlegroundsTab</c> takes them; the first is the default.</summary>
    public static IReadOnlyList<string> BattlegroundsTabs { get; } = ["Heroes", "Comps", "Trinkets", "Cards", "Mine"];

    /// <summary>The Constructed page's tabs in order, as the window's <c>ConstructedTab</c> takes them; the first is the default.</summary>
    public static IReadOnlyList<string> ConstructedTabs { get; } = ["Decks", "Tiers", "Top", "Matchups"];

    /// <summary>The page Ctrl+<paramref name="digit"/> opens, or null when no page has that number.</summary>
    public static string? NavPageForDigit(int digit) => digit >= 1 && digit <= NavPages.Count ? NavPages[digit - 1] : null;

    /// <summary>
    /// Which button of a page or tab list (<see cref="NavPages"/>, <see cref="BattlegroundsTabs"/>,
    /// <see cref="ConstructedTabs"/>) opens <paramref name="name"/>: its index, the first for an unknown name.
    /// </summary>
    public static int NavIndex(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The name of the checked button of a page or tab list, from the buttons' states in the list's order; null while
    /// none is checked (a radio group between two buttons).
    /// </summary>
    public static string? NavChecked(IReadOnlyList<string> names, IReadOnlyList<bool?> isChecked)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (isChecked[i] == true)
            {
                return names[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The window now shows <paramref name="page"/> (null while no sidebar button is checked). Leaving History lets go of
    /// the open match: only the match on screen is kept in memory. Standard and Wild are one page shown for a format.
    /// </summary>
    public void PageShown(string? page)
    {
        if (page is null)
        {
            return;
        }

        if (MatchOpen && page != "History")
        {
            CloseMatch();
        }

        if (page is ConstructedPage.Standard or ConstructedPage.Wild)
        {
            ShowConstructedFormat(ConstructedPage.FormatOf(page));
        }
    }

    /// <summary>
    /// Leaves the detail page on <paramref name="page"/>, like its Back button: the match page goes back to History,
    /// the deck page to the deck list. False when that page shows no detail, so the key or mouse button is left alone.
    /// </summary>
    public bool GoBack(string page)
    {
        if (page == "History" && MatchOpen)
        {
            CloseMatch();
            return true;
        }

        if (page is ConstructedPage.Standard or ConstructedPage.Wild && DeckOpen)
        {
            CloseDeck();
            return true;
        }

        return false;
    }
}
