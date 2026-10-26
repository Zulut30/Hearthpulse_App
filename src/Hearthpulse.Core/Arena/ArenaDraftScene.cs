using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Arena;

/// <summary>
/// Render-check scenes of the Arena draft helper (<c>overlay-arena-hero</c>, <c>overlay-arena-heropower</c>,
/// <c>overlay-arena-pick</c>, <c>overlay-arena-package</c>): a made-up Warlock draft built from the real scores and card
/// data, since no Arena.log is kept on the development machine.
/// </summary>
public static class ArenaDraftScene
{
    private const string Warlock = "HERO_07";

    /// <summary>The hero pick of an Underground run: Warlock, Mage and Warrior on offer.</summary>
    public static ArenaDraftView HeroPick(ArenaMeta meta, CardDb cards) =>
        ArenaDraft.Build(ArenaDraftState.Empty, [Warlock, "HERO_08", "HERO_01"], meta, cards, underground: true);

    /// <summary>The hero power pick after the Warlock: three hero powers, no numbers (only the panel shows).</summary>
    public static ArenaDraftView HeroPowerPick(ArenaMeta meta, CardDb cards) =>
        ArenaDraft.Build(new ArenaDraftState(Warlock, 0, [], "DRAFTING"), ["HERO_07bp", "HERO_08bp", "HERO_01bp"], meta, cards, underground: true);

    /// <summary>
    /// Pick 15 of a normal-Arena Warlock draft: 14 cards of every tier taken, a strong, a middling and a weak card on
    /// offer; the scores are the server's Underground slice, so the panel says so.
    /// </summary>
    public static ArenaDraftView CardPick(ArenaMeta meta, CardDb cards)
    {
        var pool = Pool(meta, cards);
        if (pool.Count < 16)
        {
            return ArenaDraft.Build(new ArenaDraftState(Warlock, 0, pool, "DRAFTING"), pool.Take(3).ToList(), meta, cards, underground: false);
        }

        IReadOnlyList<string> offer = [pool[pool.Count * 3 / 4], pool[0], pool[pool.Count / 2]];
        return ArenaDraft.Build(new ArenaDraftState(Warlock, 0, Deck(pool), "DRAFTING"), offer, meta, cards, underground: false);
    }

    /// <summary>An Underground pick where the middle card is a legendary that brings two more cards.</summary>
    public static ArenaDraftView PackagePick(ArenaMeta meta, CardDb cards)
    {
        var pool = Pool(meta, cards);
        if (pool.Count < 16)
        {
            return CardPick(meta, cards);
        }

        var legendary = pool.FirstOrDefault(id => cards.Get(id)?.Rarity == "LEGENDARY") ?? pool[1];
        IReadOnlyList<string> ids = [pool[pool.Count / 3], legendary, pool[pool.Count * 2 / 3]];
        IReadOnlyList<IReadOnlyList<string>> packages = [[], [pool[pool.Count / 4], pool[pool.Count / 5]], []];
        return ArenaDraft.Build(new ArenaDraftState(Warlock, 0, Deck(pool), "DRAFTING"), new ArenaDraftOffer(ids, true, packages), meta, cards);
    }

    /// <summary>The 14 cards taken so far: every tier, one of them twice.</summary>
    private static List<string> Deck(List<string> pool)
    {
        var deck = Enumerable.Range(1, 13).Select(i => pool[i * pool.Count / 15]).ToList();
        deck.Add(deck[2]);
        return deck;
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
