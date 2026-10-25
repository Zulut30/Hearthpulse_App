namespace Hearthpulse.Core.Stats;

/// <summary>
/// The Standard and Wild pages of the main window: one Constructed page shown for one game format. My stats, your decks,
/// merge suggestions, matchups and the meta all follow the page's format.
/// </summary>
public static class ConstructedPage
{
    public const string Standard = "Standard";
    public const string Wild = "Wild";

    /// <summary>A page name as ShowPage takes it: the single "Constructed" page of older versions opens as Standard.</summary>
    public static string Migrate(string? page) => page == "Constructed" ? Standard : page ?? string.Empty;

    /// <summary>The page a game FormatType opens on: Wild for FT_WILD, Standard for everything else.</summary>
    public static string PageOf(string? formatType) => formatType == "FT_WILD" ? Wild : Standard;

    /// <summary>The FormatType a page shows: "FT_WILD" for Wild, "FT_STANDARD" otherwise.</summary>
    public static string FormatOf(string? page) => page == Wild ? "FT_WILD" : "FT_STANDARD";

    /// <summary>
    /// The decks a page lists, in the order given: Wild lists Wild decks, Standard every other one (Twist, Classic and
    /// other formats have no page of their own, and a deck opens where it is listed, see <see cref="PageOf"/>).
    /// </summary>
    public static IReadOnlyList<DeckFamily> Decks(IEnumerable<DeckFamily> families, string formatType) =>
        families.Where(f => PageOf(f.Format) == PageOf(formatType)).ToList();

    /// <summary>The merge suggestions a page lists (both decks of a pair always share their format).</summary>
    public static IReadOnlyList<MergeSuggestion> Suggestions(IEnumerable<MergeSuggestion> suggestions, string formatType) =>
        suggestions.Where(s => PageOf(s.First.Format) == PageOf(formatType)).ToList();
}
