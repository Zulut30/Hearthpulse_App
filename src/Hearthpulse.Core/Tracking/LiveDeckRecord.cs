using Hearthpulse.Core.Data;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// The record of the deck being played and its matchup line, which both apps show in the overlay's deck panel. The
/// hosts look it up off the UI thread whenever <see cref="Key"/> changes.
/// </summary>
public static class LiveDeckRecord
{
    /// <summary>What the lookup depends on (deck, its code and id, both classes); null outside a Constructed game.</summary>
    public static string? Key(ConstructedView? view) =>
        view is null ? null : $"{view.DeckName}|{view.DeckId}|{view.DeckCode}|{view.PlayerClass}|{view.OpponentClass}";

    /// <summary>Reads the history (and Decks.log, for the deck's other codes); call it off the UI thread.</summary>
    /// <param name="installDir">The Hearthstone folder, when known.</param>
    public static (DeckAggregate? Deck, MatchupAggregate? Matchup) Read(HistoryStore history, string? installDir, ConstructedView view)
    {
        var sameDeck = installDir is { } dir ? DecksLog.CodesOf(DecksLog.ReadAll(dir), view.DeckId) : [];
        var all = history.Recent(int.MaxValue, "Constructed");
        // Versions the player merged into one deck count together, as on the deck page.
        var games = DeckFamilies.WithFamily(all, DeckRecords.Matching(all, view.DeckName, view.DeckCode, view.PlayerClass, sameDeck), history.DeckFamilies());
        var deck = DeckRecords.Aggregate(games, view.DeckName, view.DeckCode, view.PlayerClass);
        // The matchup line counts the same games as the record; without any, the class matchup over all decks.
        var matchup = games.Count > 0
            ? DeckRecords.Matchup(games, view.OpponentClass)
            : history.MatchupStats().FirstOrDefault(m => m.OpponentClass == view.OpponentClass);
        return (deck, matchup);
    }
}
