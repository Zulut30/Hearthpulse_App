using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// The minions that died this game, each side's, for the "Graveyard" panel (<c>docs/graveyard.md</c>): deaths on the
/// board, which both players see. Rows by cost then name, a count for repeats; the totals count every death.
/// </summary>
public sealed record GraveyardView(IReadOnlyList<MatchListRow> Local, int LocalTotal, IReadOnlyList<MatchListRow> Opponent, int OpponentTotal)
{
    /// <summary>The minions of one side that died (counter log), as rows; a card missing from the card data is "Unknown card".</summary>
    public static IReadOnlyList<MatchListRow> Rows(IEnumerable<string> died, CardDb cards) =>
        died.Where(id => id.Length > 0)
            .GroupBy(id => id, StringComparer.Ordinal)
            .Select(g => cards.Get(g.Key) is { } card
                ? new MatchListRow(card.Id, card.Name, card.Cost, card.Rarity, g.Count())
                : new MatchListRow(g.Key, Loc.T("Related.Unknown"), 0, string.Empty, g.Count()))
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// A Graveyard for the <c>--render-ui</c> scene: a long game, ten and more different minions a side (both sides at
    /// the row limit and "+N more"), repeats and one long name.
    /// </summary>
    public static GraveyardView Sample(CardDb cards)
    {
        string[] opponent = ["CS2_182", "CS2_182", "EX1_506", "EX1_506a", "CS2_120", "EX1_096", "CS2_179", "EX1_015", "CS2_168", "CS2_172", "CS2_119", "CS2_124"];
        string[] local = ["EX1_096", "CS2_182", "NEW1_018", "EX1_029", "CS2_121", "CS2_127", "CS2_131", "CS2_141", "CS2_147", "CS2_150", "CS2_155"];
        return new GraveyardView(Rows(local, cards), local.Length, Rows(opponent, cards), opponent.Length);
    }

    /// <summary>Both sides' dead minions of <paramref name="game"/>; null before the first death.</summary>
    public static GraveyardView? Of(GameState game, CounterTracker counters, CardDb cards) =>
        Of(counters.For(game.LocalPlayerId).Died, OpponentDied(game, counters), cards);

    private static GraveyardView? Of(IReadOnlyCollection<string> local, IReadOnlyCollection<string> opponent, CardDb cards) =>
        local.Count + opponent.Count == 0 ? null : new GraveyardView(Rows(local, cards), local.Count, Rows(opponent, cards), opponent.Count);

    private static IReadOnlyCollection<string> OpponentDied(GameState game, CounterTracker counters) =>
        game.Players.Keys.FirstOrDefault(id => id != game.LocalPlayerId) is var other and > 0 ? counters.For(other).Died : [];

    /// <summary>
    /// The view for these death lists, rebuilt only when a side's count changed (deaths are only added) or the card data
    /// or language did: every publish asks for it.
    /// </summary>
    internal sealed class Cache
    {
        private IReadOnlyCollection<string>? _local;
        private IReadOnlyCollection<string>? _opponent;
        private int _localCount = -1;
        private int _opponentCount = -1;
        private CardDb? _cards;
        private string _language = string.Empty;
        private GraveyardView? _view;

        public GraveyardView? Of(GameState game, CounterTracker counters, CardDb cards)
        {
            var local = counters.For(game.LocalPlayerId).Died;
            var opponent = OpponentDied(game, counters);
            var language = Loc.Instance.Language;
            if (ReferenceEquals(local, _local) && ReferenceEquals(opponent, _opponent) && local.Count == _localCount
                && opponent.Count == _opponentCount && ReferenceEquals(cards, _cards) && language == _language)
            {
                return _view;
            }

            (_local, _opponent, _localCount, _opponentCount, _cards, _language) = (local, opponent, local.Count, opponent.Count, cards, language);
            return _view = GraveyardView.Of(local, opponent, cards);
        }
    }
}

public sealed partial class GameSession
{
    private readonly GraveyardView.Cache _graveyard = new();

    /// <summary>Adds the graveyard to a Constructed view; called while publishing.</summary>
    private ConstructedView WithGraveyard(ConstructedView view, GameState game) =>
        view with { Graveyard = _graveyard.Of(game, _constructed.Counters, _cards) };
}
