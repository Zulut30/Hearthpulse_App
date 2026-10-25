using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Arena;

/// <summary>What the Arena page says about a run's deck: its score and the cards that pull it up and down.</summary>
/// <param name="Best">The highest scored distinct cards, best first (at most three).</param>
/// <param name="Weakest">The lowest scored distinct cards, weakest first (at most three).</param>
public sealed record ArenaDeckReport(ArenaDeckScore Score, IReadOnlyList<ArenaDeckCard> Best, IReadOnlyList<ArenaDeckCard> Weakest);

/// <summary>Scores the deck of a saved Arena run (the deck code Decks.log announced before its games).</summary>
public static class ArenaRunDeck
{
    private const int Highlights = 3;

    /// <summary>The run's deck: the first deck code any of its games saved, or null.</summary>
    public static DeckDefinition? DeckOf(ArenaRun run) =>
        run.Games.Select(g => g.DeckCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => DeckString.TryDecode(code!))
            .FirstOrDefault(deck => deck is { CardCount: > 0 });

    /// <summary>The run deck's score, or null when the run has no deck code or nothing in it is scored.</summary>
    public static ArenaDeckScore? ScoreOf(ArenaRun run, ArenaMeta? meta, CardDb cards) =>
        DeckOf(run) is { } deck && ArenaDraft.ScoreOf(deck, meta, cards) is { Average: not null } score ? score : null;

    public static ArenaDeckReport? Report(ArenaRun run, ArenaMeta? meta, CardDb cards) =>
        DeckOf(run) is { } deck ? Report(deck, meta, cards) : null;

    /// <summary>Score, best and weakest cards of a deck; null when none of its cards is scored.</summary>
    public static ArenaDeckReport? Report(DeckDefinition deck, ArenaMeta? meta, CardDb cards)
    {
        var score = ArenaDraft.ScoreOf(deck, meta, cards);
        if (score.Average is null)
        {
            return null;
        }

        var distinct = deck.Cards
            .Select(c =>
            {
                var card = cards.Get(c.Key);
                var id = card?.Id ?? meta?.Card(c.Key)?.CardId ?? string.Empty;
                var value = meta?.Score(id);
                return new ArenaDeckCard(id, card?.Name ?? id, card?.Cost ?? 0, c.Value, value, ArenaMeta.TierOf(value));
            })
            .Where(c => c.Score is not null && c.CardId.Length > 0)
            .ToList();
        var best = distinct.OrderByDescending(c => c.Score).ThenBy(c => c.Cost).Take(Highlights).ToList();
        var weakest = distinct.OrderBy(c => c.Score).ThenBy(c => c.Cost).Where(c => !best.Contains(c)).Take(Highlights).ToList();
        return new ArenaDeckReport(score, best, weakest);
    }
}
