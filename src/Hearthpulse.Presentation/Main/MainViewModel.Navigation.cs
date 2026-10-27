using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>Keyboard and mouse navigation shared by both main windows: Ctrl+1…7 pages, Esc / Back to leave a detail page.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The sidebar pages in order, as the window's <c>ShowPage</c> takes them; Ctrl+1 opens the first.</summary>
    public static IReadOnlyList<string> NavPages { get; } =
        ["Overview", "Battlegrounds", ConstructedPage.Standard, ConstructedPage.Wild, "Arena", "History", "Settings"];

    /// <summary>The page Ctrl+<paramref name="digit"/> opens, or null when no page has that number.</summary>
    public static string? NavPageForDigit(int digit) => digit >= 1 && digit <= NavPages.Count ? NavPages[digit - 1] : null;

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
