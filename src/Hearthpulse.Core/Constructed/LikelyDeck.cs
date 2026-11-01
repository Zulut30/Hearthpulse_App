using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Constructed;

/// <summary>One card of the opponent's likely list: copies in the list and how many the opponent has played.</summary>
/// <param name="Played">Copies played face up from the starting deck.</param>
public sealed record LikelyDeckRow(string CardId, string Name, int Cost, string Rarity, int Copies, int Played)
{
    public bool AllPlayed => Played >= Copies;
}

/// <summary>
/// The opponent's likely deck while the game runs (<c>docs/opponent-deck.md</c>): the most played meta list of the
/// leading archetype, the played copies counted, and the played starting-deck cards the list does not hold.
/// </summary>
/// <param name="ArchetypeKey">The English archetype name (shown translated, as the archetype panel does).</param>
/// <param name="Chance">The archetype's chance (<see cref="ArchetypeOdds"/>), 0–1.</param>
/// <param name="Rank">The meta's rank key (<see cref="ConstructedMeta.Ranks"/>), from the meta page's filters.</param>
/// <param name="Period">The meta's period key (<see cref="ConstructedMeta.Periods"/>).</param>
/// <param name="Games">Games of the list; 0 when only the archetype's core cards are known.</param>
public sealed record LikelyDeckView(string ArchetypeKey, double Chance, IReadOnlyList<LikelyDeckRow> Rows, IReadOnlyList<LikelyDeckRow> OffList,
    string Rank, string Period, int Games)
{
    /// <summary>The archetype has no lists in the meta: the rows are its core cards, one copy each.</summary>
    public bool CoreOnly => Games == 0;

    /// <summary>
    /// A likely deck for the <c>--render-ui</c> scene: a long Priest list (more rows than fit, so it scrolls), copies
    /// untouched, one of two played, all played, played legendaries, and two cards off the list.
    /// </summary>
    public static LikelyDeckView Sample(CardDb cards)
    {
        (string Id, int Copies, int Played)[] list =
        [
            ("CS1_130", 2, 1), ("CS2_004", 2, 2), ("CS2_234", 2, 0), ("CS2_235", 2, 1), ("EX1_621", 2, 0), ("CS1_112", 2, 0),
            ("EX1_622", 2, 1), ("EX1_591", 1, 0), ("CS2_181", 2, 0), ("EX1_085", 1, 1), ("EX1_350", 1, 1), ("EX1_339", 2, 0),
            ("CS1_129", 1, 0), ("EX1_016", 1, 0), ("CS1_113", 2, 0), ("NEW1_030", 1, 0),
        ];
        (string Id, int Played)[] off = [("CS2_182", 2), ("EX1_506", 1)];
        LikelyDeckRow Row(string id, int copies, int played) =>
            new(id, cards.Get(id)?.Name ?? id, cards.Get(id)?.Cost ?? 0, cards.Get(id)?.Rarity ?? string.Empty, copies, played);
        return new LikelyDeckView("Control Priest", 0.62,
            list.Select(c => Row(c.Id, c.Copies, c.Played)).OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture).ToList(),
            off.Select(c => Row(c.Id, c.Played, c.Played)).OrderBy(r => r.Cost).ToList(), "legend", "past_week", 4210);
    }
}

/// <summary>
/// The likely list of an archetype from public meta, with the cards the opponent showed. Only meta and face-up
/// starting-deck cards go in: a card created during the game or still in the opponent's hand never does.
/// </summary>
public static class LikelyDeck
{
    /// <summary>
    /// The archetype's most played list (by games) as card ids and copies, and its games; else its core cards, one
    /// copy each, and 0 games. Null when neither is known.
    /// </summary>
    public static (IReadOnlyList<(string Id, int Copies)> Cards, int Games)? ListOf(ConstructedMeta meta, MetaArchetype archetype, CardDb cards)
    {
        if (meta.Decks.Where(d => d.ArchetypeId == archetype.Id).MaxBy(d => d.Games) is { } top && DeckString.TryDecode(top.DeckString) is { Cards.Count: > 0 } deck)
        {
            var list = deck.Cards.Select(kv => (Id: cards.Get(kv.Key)?.Id ?? string.Empty, Copies: kv.Value)).Where(c => c.Id.Length > 0).ToList();
            if (list.Count > 0)
            {
                return (list, Math.Max(1, top.Games));
            }
        }

        var core = archetype.CoreCards.Distinct(StringComparer.Ordinal).Select(id => (Id: id, Copies: 1)).ToList();
        return core.Count > 0 ? (core, 0) : null;
    }

    /// <summary>
    /// The view for the archetype that leads <paramref name="odds"/>, or null without odds, the archetype in the meta
    /// or a list.
    /// </summary>
    /// <param name="playedFromDeck">Copies of each card the opponent played face up from its starting deck.</param>
    public static LikelyDeckView? Build(ConstructedMeta? meta, OpponentArchetypeView? odds, string? heroClass, IReadOnlyDictionary<string, int> playedFromDeck, CardDb cards)
    {
        if (meta is null || odds is null || odds.Archetypes.Count == 0
            || meta.ForClass(heroClass).FirstOrDefault(a => a.Key == odds.Best.Key) is not { } archetype
            || ListOf(meta, archetype, cards) is not { } list)
        {
            return null;
        }

        LikelyDeckRow Row(string id, int copies, int played)
        {
            var card = cards.Get(id);
            return new LikelyDeckRow(id, card?.Name ?? id, card?.Cost ?? 0, card?.Rarity ?? string.Empty, copies, played);
        }

        var inList = new HashSet<string>(list.Cards.Select(c => c.Id), StringComparer.Ordinal);
        // A card played more often than the list runs it: the opponent runs more copies.
        var rows = Sorted(list.Cards.Select(c =>
        {
            var played = playedFromDeck.GetValueOrDefault(c.Id);
            return Row(c.Id, Math.Max(c.Copies, played), played);
        }));
        var offList = Sorted(playedFromDeck.Where(p => p.Value > 0 && !inList.Contains(p.Key)).Select(p => Row(p.Key, p.Value, p.Value)));
        return new LikelyDeckView(odds.Best.Key, odds.Best.Chance, rows, offList, meta.Rank, meta.Period, list.Games);
    }

    private static List<LikelyDeckRow> Sorted(IEnumerable<LikelyDeckRow> rows) =>
        rows.OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture).ToList();
}
