using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// <c>--render-ui</c> data for the archetype, turn timer and ladder session widgets, the same in both UIs: the real
/// archetype odds of a kept log's opponent when the meta allows, otherwise made-up numbers on real card ids.
/// </summary>
public static class WidgetScene
{
    /// <summary>The view with an archetype and a turn clock in which the local turn burns the rope.</summary>
    public static ConstructedView InGame(ConstructedView view, ConstructedMeta? meta, CardDb cards, DateTime now, bool localRope) => view with
    {
        OpponentArchetype = view.OpponentArchetype ?? Archetype(view, meta, cards),
        Turns = localRope
            ? new TurnClockView(6, true, now.AddSeconds(-63), 75, TimeSpan.FromSeconds(34), TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(52), TimeSpan.FromSeconds(28))
            : new TurnClockView(6, false, now.AddSeconds(-22), 75, TimeSpan.FromSeconds(34), TimeSpan.FromSeconds(41), TimeSpan.FromSeconds(52), TimeSpan.FromSeconds(28)),
    };

    private static OpponentArchetypeView? Archetype(ConstructedView view, ConstructedMeta? meta, CardDb cards)
    {
        var format = meta?.Format == "wild" ? "FT_WILD" : "FT_STANDARD";
        var shown = view.OpponentPlayed.Where(p => !p.IsCreated).Select(p => p.CardId).ToList();
        if (ArchetypeOdds.Build(meta, "GT_RANKED", format, view.OpponentClass, shown, cards) is { } real)
        {
            return real;
        }

        // The meta's most played archetype, as if its opponent had shown two of its core cards.
        if (meta?.Archetypes.FirstOrDefault(a => a.CoreCards.Count >= 2) is { } popular
            && ArchetypeOdds.Build(meta, "GT_RANKED", format, popular.Class, popular.CoreCards.Take(2).ToList(), cards) is { } guessed)
        {
            return guessed;
        }

        var deck = view.Deck.Take(5).ToList();
        if (deck.Count == 0)
        {
            return null;
        }

        return new OpponentArchetypeView(
            [new("Control Priest", "Control Priest", 0.71, 0.534, 41000), new("Zarimi Priest", "Zarimi Priest", 0.19, 0.51, 22000), new("Aggro Priest", "Aggro Priest", 0.07, 0.49, 9000)],
            deck.Select((r, i) => new LikelyCard(r.CardId, r.Name, r.Cost, 0.92 - (i * 0.13))).ToList(),
            3,
            ConstructedMeta.Attribution);
    }

    /// <summary>A Standard climb and a Wild Legend session that began <paramref name="now"/> minus 1 h 40 min.</summary>
    public static LadderSessions Ladder(DateTime now)
    {
        var since = now.AddMinutes(-100);
        return new LadderSessions(
            new LadderSessionView(LadderSession.Standard, since, new LadderRank(46, null), new LadderRank(49, null), new WinLoss(7, 5, 2), 3),
            new LadderSessionView(LadderSession.Wild, since, new LadderRank(51, 1520), new LadderRank(51, 1204), new WinLoss(3, 2, 1), -1));
    }
}
