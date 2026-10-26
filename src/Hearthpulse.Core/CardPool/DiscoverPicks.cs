using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Pool;

namespace Hearthpulse.Core.CardPool;

/// <summary>One offered card of an open choice, with what pool players did when it was offered by the same card.</summary>
/// <param name="PickRate">Share of offers in which it was taken (0..1); null below <see cref="DiscoverPicks.MinOffers"/>.</param>
/// <param name="WinRate">Share of games won after taking it (0..1); null below <see cref="DiscoverPicks.MinPicks"/>.</param>
/// <param name="Top">The most taken option, when its numbers are shown and it leads the others clearly.</param>
public sealed record DiscoverPickRow(string CardId, string Name, int Offered, int Picked, double? PickRate, double? WinRate, bool Top);

/// <summary>The pick hint of an open choice, in offer order.</summary>
/// <param name="Choices">Choices from this source card in the pool's window (0 when the pool has none yet).</param>
public sealed record DiscoverPicksView(string SourceCardId, int Choices, IReadOnlyList<DiscoverPickRow> Rows)
{
    /// <summary>Whether any option has enough offers for a number.</summary>
    public bool HasNumbers => Rows.Any(r => r.PickRate is not null);
}

/// <summary>
/// Builds the pick hint of the player's own open choice from the stats pool's totals (<see cref="DiscoverPickStats"/>):
/// only the cards on the player's screen, only what pool players did. Numbers, no advice.
/// </summary>
public static class DiscoverPicks
{
    /// <summary>Offers of one option from one source before its pick rate is shown.</summary>
    public const int MinOffers = 20;

    /// <summary>Picks of one option before the win rate after taking it is shown.</summary>
    public const int MinPicks = 20;

    /// <summary>How far the most taken option must lead the next for the "most taken" mark (pick rate points).</summary>
    public const double TopLead = 0.05;

    /// <param name="source">The card id the log named as the choice's source, as the tracker shares it.</param>
    /// <param name="offered">Card ids of the options on screen, in offer order.</param>
    public static DiscoverPicksView Build(DiscoverPickStats stats, string source, IReadOnlyList<string> offered, CardDb cards)
    {
        var picks = stats.Sources.TryGetValue(source, out var s) ? s : null;
        var rows = offered.Select(id =>
        {
            var option = picks?.Options.TryGetValue(id, out var o) == true ? o : new PickOption(0, 0, 0);
            return new DiscoverPickRow(
                id,
                cards.Get(id)?.Name ?? id,
                option.Offered,
                option.Picked,
                option.Offered >= MinOffers ? (double)option.Picked / option.Offered : null,
                option.Picked >= MinPicks ? (double)option.Wins / option.Picked : null,
                false);
        }).ToList();

        var ranked = rows.Where(r => r.PickRate is not null).OrderByDescending(r => r.PickRate).ToList();
        if (ranked.Count >= 2 && ranked[0].PickRate - ranked[1].PickRate >= TopLead)
        {
            var top = rows.IndexOf(ranked[0]);
            rows[top] = rows[top] with { Top = true };
        }

        return new DiscoverPicksView(source, picks?.Choices ?? 0, rows);
    }

    /// <summary>
    /// --render-ui only: a hint for three options of <paramref name="pool"/> with made-up totals (one clearly most
    /// taken, one under the win rate floor), so both UIs can be checked without a running stats pool.
    /// </summary>
    public static DiscoverPicksView RenderSample(CardPoolView pool, CardDb cards)
    {
        var offered = pool.Cards.Take(3).Select(c => c.CardId).ToList();
        var totals = new (int Offered, int Picked, int Wins)[] { (412, 236, 131), (398, 109, 52), (405, 14, 6) };
        var options = offered.Select((id, i) => (id, totals[i])).ToDictionary(x => x.id, x => new PickOption(x.Item2.Offered, x.Item2.Picked, x.Item2.Wins));
        var stats = new DiscoverPickStats("standard", 1840, new Dictionary<string, SourcePicks> { [pool.SourceCardId] = new(359, options) });
        return Build(stats, pool.SourceCardId, offered, cards);
    }

    /// <summary>The pool's format key of a game format, or null for formats the pool does not count.</summary>
    public static string? FormatOf(string formatType) => formatType switch
    {
        "FT_STANDARD" => "standard",
        "FT_WILD" => "wild",
        _ => null,
    };
}
