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

    /// <summary>The decks of one FormatType, in the order given.</summary>
    public static IReadOnlyList<DeckFamily> Decks(IEnumerable<DeckFamily> families, string formatType) =>
        families.Where(f => string.Equals(f.Format, formatType, StringComparison.Ordinal)).ToList();

    /// <summary>The merge suggestions of one FormatType (both decks of a pair always share it).</summary>
    public static IReadOnlyList<MergeSuggestion> Suggestions(IEnumerable<MergeSuggestion> suggestions, string formatType) =>
        suggestions.Where(s => string.Equals(s.First.Format, formatType, StringComparison.Ordinal)).ToList();
}
