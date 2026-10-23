using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Stats;

/// <summary>
/// Every string of the deck page's Versions card (versions, merge suggestions, adding a deck, undo), in one place for
/// both UIs: views read the <c>Deck.Versions.*</c> keys directly (i:Loc), view models call the formatters. The texts
/// are in the interface language of the moment they are read.
/// </summary>
public static class DeckVersionsText
{
    public static string Title => Loc.T("Deck.Versions.Title");

    public static string MainChip => Loc.T("Deck.Versions.MainChip");

    public static string MainChipTip => Loc.T("Deck.Versions.MainChipTip");

    public static string MoreTip => Loc.T("Deck.Versions.MoreTip");

    public static string MakeMain => Loc.T("Deck.Versions.MakeMain");

    public static string SplitOff => Loc.T("Deck.Versions.SplitOff");

    public static string SplitOffTip => Loc.T("Deck.Versions.SplitOffTip");

    public static string ShowDiff => Loc.T("Deck.Versions.ShowDiff");

    public static string HideDiff => Loc.T("Deck.Versions.HideDiff");

    public static string DiffTip => Loc.T("Deck.Versions.DiffTip");

    public static string FewGames => Loc.T("Deck.Versions.FewGames");

    public static string FewGamesTip => Loc.F("Deck.Versions.FewGamesTip", DeckDetails.SmallSample);

    public static string WinRateTip => Loc.T("Deck.Versions.WinRateTip");

    public static string Rename => Loc.T("Deck.Versions.Rename");

    public static string RenameTip => Loc.T("Deck.Versions.RenameTip");

    public static string NamePlaceholder => Loc.T("Deck.Versions.NamePlaceholder");

    public static string Save => Loc.T("Deck.Versions.Save");

    public static string Cancel => Loc.T("Deck.Versions.Cancel");

    public static string SuggestionsTitle => Loc.T("Deck.Versions.SuggestionsTitle");

    public static string Merge => Loc.T("Deck.Versions.Merge");

    public static string MergeTip => Loc.T("Deck.Versions.MergeTip");

    public static string NotSame => Loc.T("Deck.Versions.NotSame");

    public static string NotSameTip => Loc.T("Deck.Versions.NotSameTip");

    public static string ShowCards => Loc.T("Deck.Versions.ShowCards");

    public static string AddDeck => Loc.T("Deck.Versions.AddDeck");

    public static string AddDeckTip => Loc.T("Deck.Versions.AddDeckTip");

    public static string PickerTitle => Loc.T("Deck.Versions.PickerTitle");

    public static string SearchPlaceholder => Loc.T("Deck.Versions.SearchPlaceholder");

    public static string AddChosen => Loc.T("Deck.Versions.AddChosen");

    public static string PickFirst => Loc.T("Deck.Versions.PickFirst");

    public static string SharedTip => Loc.T("Deck.Versions.SharedTip");

    public static string MarkedDifferent => Loc.T("Deck.Versions.MarkedDifferent");

    public static string Undo => Loc.T("Deck.Versions.Undo");

    public static string Games(int games) => Loc.P("Deck.Games", games);

    /// <summary>"4–6".</summary>
    public static string Record(WinLoss record) => $"{record.Wins}–{record.Losses}";

    /// <summary>"73 % shared cards", or "no card list" when either deck has no code.</summary>
    public static string Shared(double overlap) => overlap > 0
        ? Loc.F("Deck.Versions.Shared", overlap.ToString("P0", CultureInfo.CurrentCulture))
        : Loc.T("Deck.Versions.NoCardList");

    /// <summary>"73 % shared".</summary>
    public static string SharedShort(double overlap) => overlap > 0
        ? Loc.F("Deck.Versions.SharedShort", overlap.ToString("P0", CultureInfo.CurrentCulture))
        : Loc.T("Deck.Versions.NoCardList");

    /// <summary>"73 % shared cards · similar names · 4–6" (the other deck's record, when given).</summary>
    public static string SuggestionDetail(double overlap, bool sameDeckSlot, bool similarNames, WinLoss? record = null) => string.Join(" · ", new[]
    {
        Shared(overlap),
        sameDeckSlot ? Loc.T("Deck.Versions.SameSlot") : null,
        similarNames ? Loc.T("Deck.Versions.SimilarNames") : null,
        record is null ? null : Record(record),
    }.Where(s => s is not null));

    /// <summary>"+2 / −2 cards".</summary>
    public static string CardChanges(int added, int removed) => Loc.P("Deck.Versions.CardChanges", added + removed, added, removed);

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

    public static string Merged(string name) => Loc.F("Deck.Versions.Merged", name);

    public static string NoCandidates(string playerClass, string format) => Loc.F("Deck.Versions.NoCandidates", playerClass, format);

    public static string NoMatch(string query) => Loc.F("Deck.Versions.NoMatch", query);
}
