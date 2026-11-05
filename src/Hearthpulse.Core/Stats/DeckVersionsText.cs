using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Stats;

/// <summary>
/// The strings of the deck page's Versions card that view models build (versions, merge suggestions, adding a deck),
/// for both UIs; the fixed labels are <c>Deck.Versions.*</c> keys the views read directly (i:Loc). The texts are in the
/// interface language of the moment they are read.
/// </summary>
public static class DeckVersionsText
{
    public static string ShowDiff => Loc.T("Deck.Versions.ShowDiff");

    public static string HideDiff => Loc.T("Deck.Versions.HideDiff");

    public static string FewGamesTip => Loc.F("Deck.Versions.FewGamesTip", DeckDetails.SmallSample);

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
