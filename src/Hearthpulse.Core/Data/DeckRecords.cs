using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Logs;

namespace Hearthpulse.Core.Data;

/// <summary>A history game that gets the deck Decks.log names for it.</summary>
public sealed record DeckFill(string GameKey, string DeckName, string DeckCode);

/// <summary>Finds the player's own results with a deck, and the deck of games recorded without one.</summary>
public static class DeckRecords
{
    /// <summary>The most a queue can take: an older "Finding Game With Deck" is not trusted for a game.</summary>
    public static readonly TimeSpan MaxQueue = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Win/loss with a deck. A game with a deck code counts when its card list matches the deck, or one of
    /// <paramref name="sameDeckCodes"/> (older lists of the same deck id); a game saved without a code counts when it
    /// has the deck's name and class.
    /// </summary>
    public static DeckAggregate? Find(IEnumerable<GameRecord> games, string? deckName, string? deckCode, string playerClass,
        IEnumerable<string>? sameDeckCodes = null) =>
        Aggregate(Matching(games, deckName, deckCode, playerClass, sameDeckCodes), deckName, deckCode, playerClass);

    /// <summary>The games <see cref="Find"/> counts for a deck.</summary>
    public static IReadOnlyList<GameRecord> Matching(IEnumerable<GameRecord> games, string? deckName, string? deckCode, string playerClass,
        IEnumerable<string>? sameDeckCodes = null)
    {
        var lists = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in (sameDeckCodes ?? []).Append(deckCode))
        {
            if (code is { Length: > 0 })
            {
                lists.Add(CardList(code));
            }
        }

        return games
            .Where(g => !g.IsBattlegrounds && g.Mode != "Mercenaries" && !g.IsAbandoned)
            .Where(g => g.DeckCode is { Length: > 0 } code
                ? lists.Contains(CardList(code))
                : deckName is { Length: > 0 } && g.DeckName == deckName && g.PlayerClass == playerClass)
            .ToList();
    }

    /// <summary>Win/loss over <paramref name="matched"/> (from <see cref="Matching"/>); null when there are none.</summary>
    public static DeckAggregate? Aggregate(IReadOnlyList<GameRecord> matched, string? deckName, string? deckCode, string playerClass)
    {
        if (matched.Count == 0)
        {
            return null;
        }

        var latest = matched.MaxBy(g => g.StartedAt)!;
        return new DeckAggregate(deckName ?? latest.DeckName ?? string.Empty, deckCode ?? latest.DeckCode, playerClass, matched.Count,
            matched.Count(g => g.Result == "Win"), matched.Count(g => g.Result == "Loss"), latest.StartedAt);
    }

    /// <summary>Results against one opponent class over the same games as the deck's record; null when there are none.</summary>
    public static MatchupAggregate? Matchup(IEnumerable<GameRecord> matched, string opponentClass)
    {
        var games = matched.Where(g => g.OpponentClass == opponentClass && opponentClass.Length > 0).ToList();
        return games.Count == 0
            ? null
            : new MatchupAggregate(opponentClass, games.Count, games.Count(g => g.Result == "Win"), games.Count(g => g.Result == "Loss"));
    }

    /// <summary>
    /// The deck of each Constructed game saved without a code: the last "Finding Game With Deck" before the game
    /// started, when it came after the previous game began, at most <see cref="MaxQueue"/> earlier, and its hero class
    /// is the game's class. Anything less certain is left empty.
    /// </summary>
    public static IReadOnlyList<DeckFill> MatchFindings(IEnumerable<GameRecord> games, IEnumerable<DecksLogEntry> entries, Func<string, string?> classOfCode)
    {
        var findings = entries.Where(e => e.FindingGame).OrderBy(e => e.At).ToList();
        var fills = new List<DeckFill>();
        var previousStart = DateTime.MinValue;
        foreach (var game in games.OrderBy(g => g.StartedAt))
        {
            var since = previousStart;
            previousStart = game.StartedAt;
            if (game.IsBattlegrounds || game.Mode == "Mercenaries" || game.DeckCode is { Length: > 0 } || game.PlayerClass.Length == 0)
            {
                continue;
            }

            var finding = findings.LastOrDefault(f => f.At <= game.StartedAt && f.At > since);
            if (finding is not null && game.StartedAt - finding.At <= MaxQueue && classOfCode(finding.Code) == game.PlayerClass)
            {
                fills.Add(new DeckFill(game.Key, finding.Name, finding.Code));
            }
        }

        return fills;
    }

    /// <summary>The class of a deck code's hero, or null when the code or the hero is unknown.</summary>
    public static string? ClassOf(string code, CardDb cards) =>
        DeckString.TryDecode(code, string.Empty, 0) is { HeroDbfIds.Count: > 0 } deck ? cards.Get(deck.HeroDbfIds[0])?.CardClass : null;

    /// <summary>Order-free card list of a code, so two codes of the same list compare equal; the code itself if it does not decode.</summary>
    private static string CardList(string code) =>
        DeckString.TryDecode(code, string.Empty, 0) is { } deck
            ? string.Join(',', deck.Cards.OrderBy(c => c.Key).Select(c => $"{c.Key}x{c.Value}"))
            : code;
}
