using System.Globalization;

namespace Hearthpulse.Core.Stats;

/// <summary>
/// Every string of the deck page's Versions card (versions, merge suggestions, adding a deck, undo), in one place for
/// both UIs: views read them with <c>x:Static</c>, view models call the formatters. The RU/EN pass swaps the bodies.
/// </summary>
public static class DeckVersionsText
{
    public static string Title => "Versions";

    public static string MainChip => "Main version";

    public static string MainChipTip => "The main version: its name, card list and meta archetype stand for the whole deck. Pick another one in a version's ⋯ menu.";

    public static string MoreTip => "Version actions";

    public static string MakeMain => "Make main version";

    public static string SplitOff => "Take out of this deck";

    public static string SplitOffTip => "Make this version a deck of its own again. It is not suggested for this deck again.";

    public static string ShowDiff => "Show differences";

    public static string HideDiff => "Hide differences";

    public static string DiffTip => "Cards against the main version";

    public static string FewGames => "few games";

    public static string FewGamesTip => $"Fewer than {DeckDetails.SmallSample} games: too few to tell";

    public static string WinRateTip => "Win rate";

    public static string Rename => "Rename deck";

    public static string RenameTip => "Name the deck. Empty: the main version's name.";

    public static string NamePlaceholder => "Deck name";

    public static string Save => "Save";

    public static string Cancel => "Cancel";

    public static string SuggestionsTitle => "Looks like the same deck";

    public static string Merge => "Merge";

    public static string MergeTip => "Count the games of both as one deck. You can undo it right after.";

    public static string NotSame => "Not the same deck";

    public static string NotSameTip => "Never suggest these two decks together again";

    public static string ShowCards => "Show cards";

    public static string AddDeck => "Add a deck to this family…";

    public static string AddDeckTip => "Pick another deck of this class and format that is a version of this one";

    public static string PickerTitle => "Add a deck to this family";

    public static string SearchPlaceholder => "Search decks";

    public static string AddChosen => "Add to family";

    public static string PickFirst => "Pick a deck in the list first";

    public static string SharedTip => "Cards in common with this deck, copies counted";

    public static string MarkedDifferent => "you said it is another deck";

    public static string Undo => "Undo";

    public static string Games(int games) => games == 1 ? "1 game" : $"{games.ToString("N0", CultureInfo.CurrentCulture)} games";

    /// <summary>"4–6".</summary>
    public static string Record(WinLoss record) => $"{record.Wins}–{record.Losses}";

    /// <summary>"73 % shared cards", or "no card list" when either deck has no code.</summary>
    public static string Shared(double overlap) => overlap > 0 ? $"{overlap.ToString("P0", CultureInfo.CurrentCulture)} shared cards" : "no card list";

    /// <summary>"73 % shared".</summary>
    public static string SharedShort(double overlap) => overlap > 0 ? $"{overlap.ToString("P0", CultureInfo.CurrentCulture)} shared" : "no card list";

    /// <summary>"73 % shared cards · similar names · 4–6" (the other deck's record, when given).</summary>
    public static string SuggestionDetail(double overlap, bool sameDeckSlot, bool similarNames, WinLoss? record = null) => string.Join(" · ", new[]
    {
        Shared(overlap),
        sameDeckSlot ? "same deck slot" : null,
        similarNames ? "similar names" : null,
        record is null ? null : Record(record),
    }.Where(s => s is not null));

    /// <summary>"+2 / −2 cards".</summary>
    public static string CardChanges(int added, int removed) => $"+{added} / −{removed} {(added + removed == 1 ? "card" : "cards")}";

    /// <summary>"2 games · Today 16:09 – 16:33": the day is written once when both ends share it ("... HH:mm").</summary>
    public static string VersionDetail(int games, string first, string last)
    {
        var span = first == last ? first
            : first.Length > 5 && last.Length > 5 && first[..^5] == last[..^5] ? $"{first} – {last[^5..]}"
            : $"{first} – {last}";
        return $"{Games(games)} · {span}";
    }

    /// <summary>"10 games · 4–6 · Today 16:33".</summary>
    public static string CandidateDetail(WinLoss record, string lastPlayed) => $"{Games(record.Games)} · {Record(record)} · {lastPlayed}";

    public static string Merged(string name) => $"Merged “{name}” into this deck";

    public static string NoCandidates(string playerClass, string format) => $"No other {playerClass} {format} deck to add yet. Decks show up here once you finish a game with them.";

    public static string NoMatch(string query) => $"No deck matches “{query}”.";
}
