using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// The minions that died this game, each side's, for the "Graveyard" panel (<c>docs/graveyard.md</c>): deaths on the
/// board, which both players see. Rows by cost then name, a count for repeats; the totals count every death.
/// </summary>
public sealed record GraveyardView(IReadOnlyList<MatchListRow> Local, int LocalTotal, IReadOnlyList<MatchListRow> Opponent, int OpponentTotal)
{
    /// <summary>The minions of one side that died (counter log), as rows.</summary>
    public static IReadOnlyList<MatchListRow> Rows(IEnumerable<string> died, CardDb cards) =>
        died.Where(id => id.Length > 0)
            .GroupBy(id => id, StringComparer.Ordinal)
            .Select(g => cards.Get(g.Key) is { } card
                ? new MatchListRow(card.Id, card.Name, card.Cost, card.Rarity, g.Count())
                : new MatchListRow(g.Key, g.Key, 0, string.Empty, g.Count()))
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>A Graveyard for the <c>--render-ui</c> scene: common minions on both sides, repeats and one long name.</summary>
    public static GraveyardView Sample(CardDb cards)
    {
        string[] opponent = ["CS2_182", "CS2_182", "EX1_506", "EX1_506a", "CS2_120", "EX1_096", "CS2_179", "EX1_015", "CS2_168", "CS2_172", "CS2_119", "CS2_124"];
        string[] local = ["EX1_096", "CS2_182", "NEW1_018", "EX1_029"];
        return new GraveyardView(Rows(local, cards), local.Length, Rows(opponent, cards), opponent.Length);
    }

    /// <summary>Both sides' dead minions of <paramref name="game"/>; null before the first death.</summary>
    public static GraveyardView? Of(GameState game, CounterTracker counters, CardDb cards)
    {
        var local = counters.For(game.LocalPlayerId).Died;
        var opponent = game.Players.Keys.FirstOrDefault(id => id != game.LocalPlayerId) is var other and > 0 ? counters.For(other).Died : [];
        return local.Count + opponent.Count == 0 ? null : new GraveyardView(Rows(local, cards), local.Count, Rows(opponent, cards), opponent.Count);
    }
}

public sealed partial class GameSession
{
    /// <summary>Adds the graveyard to a Constructed view; called while publishing.</summary>
    private ConstructedView WithGraveyard(ConstructedView view, GameState game) =>
        view with { Graveyard = GraveyardView.Of(game, _constructed.Counters, _cards) };
}
