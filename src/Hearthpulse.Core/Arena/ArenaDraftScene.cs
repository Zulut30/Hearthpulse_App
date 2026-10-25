using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Arena;

/// <summary>
/// Render-check scenes of the Arena draft helper (<c>overlay-arena-hero</c>, <c>overlay-arena-pick</c>): a made-up
/// Warlock draft built from the real scores and card data, since no Arena.log is kept on the development machine.
/// </summary>
public static class ArenaDraftScene
{
    private const string Warlock = "HERO_07";

    /// <summary>The hero pick: Warlock, Mage and Warrior on offer.</summary>
    public static ArenaDraftView HeroPick(ArenaMeta meta, CardDb cards) =>
        ArenaDraft.Build(ArenaDraftState.Empty, [Warlock, "HERO_08", "HERO_01"], meta, cards);

    /// <summary>Pick 15 of a Warlock draft: 14 cards of every tier taken, a strong, a middling and a weak card on offer.</summary>
    public static ArenaDraftView CardPick(ArenaMeta meta, CardDb cards)
    {
        var pool = Pool(meta, cards);
        if (pool.Count < 16)
        {
            return ArenaDraft.Build(new ArenaDraftState(Warlock, 0, pool, "DRAFTING"), pool.Take(3).ToList(), meta, cards);
        }

        var deck = Enumerable.Range(1, 13).Select(i => pool[i * pool.Count / 15]).ToList();
        deck.Add(deck[2]);
        IReadOnlyList<string> offer = [pool[pool.Count * 3 / 4], pool[0], pool[pool.Count / 2]];
        return ArenaDraft.Build(new ArenaDraftState(Warlock, 0, deck, "DRAFTING"), offer, meta, cards);
    }

    /// <summary>Scored collectible Warlock and neutral cards the card data knows, best first.</summary>
    private static List<string> Pool(ArenaMeta meta, CardDb cards) =>
        meta.Cards
            .Where(s => s.Score is not null && cards.Get(s.CardId) is { Collectible: true, CardClass: "WARLOCK" or "NEUTRAL", Type: "MINION" or "SPELL" or "WEAPON" })
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.CardId, StringComparer.Ordinal)
            .Select(s => s.CardId)
            .ToList();
}
