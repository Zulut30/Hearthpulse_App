using Hearthpulse.Core.Constructed;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// HSReplay mulligan numbers for one card of an archetype (rates as fractions, 0..1).
/// </summary>
/// <param name="Offered">Times the card was offered in the starting cards.</param>
/// <param name="InOpeningHand">Games the card was in hand after the mulligan, kept or drawn as a replacement.</param>
/// <param name="OpeningHandWinRate">Win rate of those games (HSReplay's "Mulligan WR").</param>
/// <param name="AvgTurnPlayed">The deck's average turn for playing the card; null when unknown (Firestone data).</param>
public sealed record GuideCard(string CardId, int DbfId, int Cost, int Offered, double KeepRate, int InOpeningHand, double? OpeningHandWinRate, double? AvgTurnPlayed);

/// <summary>One decklist HSReplay counts under an archetype: card dbf id → copies.</summary>
public sealed record GuideDeck(int Games, IReadOnlyDictionary<int, int> Cards);

/// <summary>The archetype's results against one opponent archetype.</summary>
public sealed record GuideMatchup(string OpponentClass, int Games, double WinRate);

/// <summary>
/// Two cards in the opening hand together. No server endpoint publishes this yet (docs/mulligan-v2.md, 4.2), so the
/// list stays empty and no synergy is shown; the maths below is ready for it.
/// </summary>
public sealed record GuidePair(string CardA, string CardB, int Games, double WinRate);

/// <summary>Everything the guide knows about the archetype the player's deck matched.</summary>
public sealed record GuideArchetype(int Id, string Name, string Class, IReadOnlyList<GuideCard> Cards, IReadOnlyList<GuideMatchup> Matchups, string Source)
{
    public IReadOnlyList<GuidePair> Pairs { get; init; } = [];

    /// <summary>Win rates going first / on the coin, when the source splits them (Firestone does, the API does not).</summary>
    public double? FirstWinRate { get; init; }

    public double? CoinWinRate { get; init; }

    /// <summary>The archetype's overall win rate in the same source as <see cref="FirstWinRate"/>.</summary>
    public double? WinRate { get; init; }
}

/// <summary>A card offered in the mulligan, left to right.</summary>
public sealed record OfferedCard(int EntityId, string CardId, string Name, int Cost);

public enum MulliganCall
{
    NoData,
    Keep,
    Mulligan,
}

/// <summary>Two offered cards whose results together differ from the sum of their own (needs pair data).</summary>
public sealed record MulliganSynergy(int EntityA, int EntityB, string NameA, string NameB, double Interaction, int Games);

/// <summary>What the current choice of keeps is expected to do.</summary>
/// <param name="Expected">Estimated win rate when exactly these cards are kept.</param>
/// <param name="Recommended">The same estimate for the recommended keeps.</param>
/// <param name="FullMulligan">The same estimate when every card is replaced.</param>
/// <param name="Warnings">Curve and duplicate warnings for these keeps (heuristics, marked as such in the text).</param>
public sealed record MulliganImpact(double Expected, double Recommended, double FullMulligan, int Kept, IReadOnlyList<string> Warnings, IReadOnlyList<MulliganSynergy> Synergies);

/// <summary>
/// The mulligan guide maths (docs/mulligan-v2.md, section 3). Card values are HSReplay V1 numbers: delta = win rate
/// with the card in the opening hand minus the deck's baseline. A hand is estimated additively: baseline + deltas of
/// the kept cards + one replacement delta per replaced card. Duplicates and the curve use labelled heuristics because
/// the data has no per-copy or per-turn split; synergies need pair data no endpoint serves yet.
/// </summary>
public static class MulliganGuide
{
    /// <summary>Fewer games than this and a card's numbers are too noisy to use.</summary>
    public const int MinSample = 30;

    /// <summary>A deck must share at least this much of its cards with one of an archetype's lists to count as that archetype.</summary>
    public const double MinDeckOverlap = 0.5;

    /// <summary>Class matchups with fewer games leave the baseline alone.</summary>
    public const int MinClassGames = 200;

    public const int MinPairGames = 200;

    /// <summary>Pair interactions smaller than this (in win rate, 0.015 = 1.5 points) are noise.</summary>
    public const double MinInteraction = 0.015;

    /// <summary>Cards usually played by this turn count as early plays.</summary>
    public const double EarlyTurn = 3.0;

    /// <summary>Cards usually played on this turn or later make a hand top-heavy.</summary>
    public const double LateTurn = 5.5;

    /// <summary>
    /// The archetype a deck belongs to: the one whose closest list shares the most cards (copies counted) with it, if
    /// that is at least <see cref="MinDeckOverlap"/> of the deck. Ties go to the archetype with more games.
    /// </summary>
    public static (MetaArchetype Archetype, double Overlap)? MatchArchetype(IReadOnlyDictionary<int, int> deck, IEnumerable<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks)> candidates)
    {
        var size = Math.Max(1, deck.Values.Sum());
        (MetaArchetype Archetype, double Overlap)? best = null;
        foreach (var (archetype, decks) in candidates)
        {
            var overlap = decks.Count == 0 ? 0 : decks.Max(d => deck.Sum(c => Math.Min(c.Value, d.Cards.GetValueOrDefault(c.Key)))) / (double)size;
            if (overlap >= MinDeckOverlap && (best is null || overlap > best.Value.Overlap || (overlap == best.Value.Overlap && archetype.Games > best.Value.Archetype.Games)))
            {
                best = (archetype, overlap);
            }
        }

        return best;
    }

    /// <summary>
    /// Win rate of an average opening hand in the card sample: the opening-hand-weighted mean of the cards' win rates.
    /// Every game adds its 3-4 cards, so this is the deck's win rate over the same games the card numbers come from.
    /// </summary>
    public static double? Baseline(IReadOnlyList<GuideCard> cards)
    {
        double games = 0, wins = 0;
        foreach (var c in cards)
        {
            if (c is { InOpeningHand: > 0, OpeningHandWinRate: { } rate })
            {
                games += c.InOpeningHand;
                wins += c.InOpeningHand * rate;
            }
        }

        return games > 0 ? wins / games : null;
    }

    /// <summary>How much better or worse the archetype does against a class than against the field (from matchups).</summary>
    public static double? ClassShift(IReadOnlyList<GuideMatchup> matchups, string? opponentClass)
    {
        var vs = matchups.Where(m => string.Equals(m.OpponentClass, opponentClass, StringComparison.OrdinalIgnoreCase)).ToList();
        var games = vs.Sum(m => m.Games);
        var all = matchups.Sum(m => m.Games);
        if (opponentClass is not { Length: > 0 } || games < MinClassGames || all == 0)
        {
            return null;
        }

        return vs.Sum(m => m.WinRate * m.Games) / games - matchups.Sum(m => m.WinRate * m.Games) / all;
    }

    /// <param name="deck">The player's deck as card id → copies, or null when unknown.</param>
    /// <param name="goingFirst">True going first, false on the coin, null when unknown.</param>
    public static MulliganView? Compute(GuideArchetype data, IReadOnlyList<OfferedCard> hand, IReadOnlyDictionary<string, int>? deck, string? opponentClass, bool? goingFirst, string context)
    {
        if (hand.Count == 0 || Baseline(data.Cards) is not { } baseline)
        {
            return null;
        }

        var stats = new Dictionary<string, GuideCard>(StringComparer.Ordinal);
        foreach (var c in data.Cards)
        {
            stats.TryAdd(c.CardId, c);
        }

        var classShift = ClassShift(data.Matchups, opponentClass);
        double? initiativeShift = goingFirst is { } first && data.WinRate is { } overall && (first ? data.FirstWinRate : data.CoinWinRate) is { } split
            ? split - overall
            : null;
        var baseRate = Math.Clamp(baseline + (classShift ?? 0) + (initiativeShift ?? 0), 0, 1);

        double? DeltaOf(string cardId) =>
            stats.TryGetValue(cardId, out var s) && s.Offered >= MinSample && s.InOpeningHand >= MinSample && s.OpeningHandWinRate is { } rate
                ? rate - baseline
                : null;

        var replace = ReplaceDelta(data.Cards, hand, deck, DeltaOf);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<MulliganCardStat>();
        foreach (var card in hand)
        {
            var copy = seen[card.CardId] = seen.GetValueOrDefault(card.CardId) + 1;
            stats.TryGetValue(card.CardId, out var s);
            var delta = DeltaOf(card.CardId);
            var turn = s?.AvgTurnPlayed ?? (s?.Cost ?? card.Cost);
            rows.Add(new MulliganCardStat(card.EntityId, card.CardId, card.Name,
                s is { Offered: >= MinSample } ? s.KeepRate : null,
                delta is null ? null : s!.OpeningHandWinRate,
                delta,
                s?.Offered ?? 0)
            {
                Copy = copy,
                Cost = card.Cost,
                ExpectedTurn = turn,
            });
        }

        var synergies = Synergies(data.Pairs, hand, baseline, DeltaOf);
        var view = new MulliganView(context, baseRate, rows, data.Source)
        {
            ReplaceDelta = replace,
            OpponentClass = classShift is null ? null : opponentClass,
            ClassShift = classShift,
            GoingFirst = initiativeShift is null ? null : goingFirst,
            HasPairData = data.Pairs.Count > 0,
            Synergies = synergies,
        };

        var recommended = Recommend(view);
        return view with
        {
            Recommended = recommended,
            Cards = rows.Select(r => r with { Call = Call(r, recommended) }).ToList(),
        };
    }

    /// <summary>The estimate and warnings for keeping exactly <paramref name="kept"/> (entity ids of offered cards).</summary>
    public static MulliganImpact Evaluate(MulliganView view, IReadOnlyCollection<int> kept)
    {
        var keptSet = kept.ToHashSet();
        var keptCards = view.Cards.Where(c => keptSet.Contains(c.EntityId)).ToList();
        var warnings = new List<string>();
        if (keptCards.Count > 0 && !keptCards.Any(IsEarly))
        {
            warnings.Add("No early play kept: nothing you keep is usually played by turn 3 (curve heuristic).");
        }

        var late = keptCards.Count(c => c.ExpectedTurn >= LateTurn);
        if (late >= 2)
        {
            warnings.Add($"Top-heavy: {late} kept cards are usually played on turn 6 or later (curve heuristic).");
        }

        foreach (var pair in keptCards.GroupBy(c => c.CardId).Where(g => g.Count() > 1 && !IsEarly(g.First())))
        {
            warnings.Add($"Both copies of {pair.First().Name} kept: the stats count the card once, so the second copy adds nothing measurable (duplicate heuristic).");
        }

        return new MulliganImpact(
            Expected(view, keptSet),
            Expected(view, view.Recommended.ToHashSet()),
            Expected(view, new HashSet<int>()),
            keptCards.Count,
            warnings,
            view.Synergies.Where(s => keptSet.Contains(s.EntityA) && keptSet.Contains(s.EntityB)).ToList());
    }

    /// <summary>Estimated win rate when exactly <paramref name="kept"/> stay in hand and the rest are replaced.</summary>
    public static double Expected(MulliganView view, IReadOnlySet<int> kept)
    {
        var total = view.BaseWinRate;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in view.Cards)
        {
            if (!kept.Contains(card.EntityId))
            {
                total += view.ReplaceDelta;
            }
            else if (counted.Add(card.CardId))
            {
                // Only the first kept copy carries the card's value: the numbers describe holding the card, not each copy.
                total += card.Delta ?? 0;
            }
        }

        total += view.Synergies.Where(s => kept.Contains(s.EntityA) && kept.Contains(s.EntityB)).Sum(s => s.Interaction);
        return Math.Clamp(total, 0, 1);
    }

    private static bool IsEarly(MulliganCardStat card) => card.ExpectedTurn <= EarlyTurn;

    /// <summary>
    /// The best keeps by <see cref="Expected"/> (every subset; a hand has at most five cards). Second copies are fixed
    /// beforehand by the duplicate heuristic: keep one only when it is an early play most players keep.
    /// </summary>
    private static IReadOnlyList<int> Recommend(MulliganView view)
    {
        var cards = view.Cards;
        var fixedKeep = new HashSet<int>();
        var fixedToss = new HashSet<int>();
        foreach (var card in cards.Where(c => c.Copy > 1))
        {
            (IsEarly(card) && card.KeepRate >= 0.5 ? fixedKeep : fixedToss).Add(card.EntityId);
        }

        var free = cards.Where(c => !fixedKeep.Contains(c.EntityId) && !fixedToss.Contains(c.EntityId)).ToList();
        HashSet<int>? best = null;
        var bestValue = double.MinValue;
        for (var mask = 0; mask < 1 << free.Count; mask++)
        {
            var set = new HashSet<int>(fixedKeep);
            for (var i = 0; i < free.Count; i++)
            {
                // Cards without data stay kept: there is nothing to say they should go.
                if ((mask & (1 << i)) != 0 || free[i].Delta is null)
                {
                    set.Add(free[i].EntityId);
                }
            }

            var value = Expected(view, set);
            if (best is null || value > bestValue + 1e-9 || (Math.Abs(value - bestValue) <= 1e-9 && set.Count > best.Count))
            {
                best = set;
                bestValue = value;
            }
        }

        return cards.Where(c => best!.Contains(c.EntityId)).Select(c => c.EntityId).ToList();
    }

    private static MulliganCall Call(MulliganCardStat card, IReadOnlyList<int> recommended) =>
        card.Delta is null && card.Copy == 1 ? MulliganCall.NoData
        : recommended.Contains(card.EntityId) ? MulliganCall.Keep
        : MulliganCall.Mulligan;

    /// <summary>
    /// What a replacement card is worth: the copy-weighted mean delta of the cards still in the deck (cards without
    /// data count 0). Without the decklist, how often each card was offered stands in for its copies.
    /// </summary>
    private static double ReplaceDelta(IReadOnlyList<GuideCard> cards, IReadOnlyList<OfferedCard> hand, IReadOnlyDictionary<string, int>? deck, Func<string, double?> deltaOf)
    {
        double weight = 0, sum = 0;
        if (deck is { Count: > 0 })
        {
            foreach (var (cardId, copies) in deck)
            {
                var left = copies - hand.Count(h => h.CardId == cardId);
                if (left > 0)
                {
                    weight += left;
                    sum += left * (deltaOf(cardId) ?? 0);
                }
            }
        }
        else
        {
            foreach (var c in cards.Where(c => c.Offered > 0))
            {
                weight += c.Offered;
                sum += c.Offered * (deltaOf(c.CardId) ?? 0);
            }
        }

        return weight > 0 ? sum / weight : 0;
    }

    /// <summary>Interaction = win rate with both cards minus the additive estimate for them (baseline + both deltas).</summary>
    private static List<MulliganSynergy> Synergies(IReadOnlyList<GuidePair> pairs, IReadOnlyList<OfferedCard> hand, double baseline, Func<string, double?> deltaOf)
    {
        var result = new List<MulliganSynergy>();
        for (var i = 0; i < hand.Count; i++)
        {
            for (var j = i + 1; j < hand.Count; j++)
            {
                var a = hand[i];
                var b = hand[j];
                if (a.CardId == b.CardId)
                {
                    continue;
                }

                var pair = pairs.FirstOrDefault(p => (p.CardA == a.CardId && p.CardB == b.CardId) || (p.CardA == b.CardId && p.CardB == a.CardId));
                if (pair is null || pair.Games < MinPairGames || deltaOf(a.CardId) is not { } da || deltaOf(b.CardId) is not { } db)
                {
                    continue;
                }

                var interaction = pair.WinRate - (baseline + da + db);
                if (Math.Abs(interaction) >= MinInteraction && !result.Any(s => (s.EntityA == a.EntityId || s.EntityB == a.EntityId) && (s.EntityA == b.EntityId || s.EntityB == b.EntityId)))
                {
                    result.Add(new MulliganSynergy(a.EntityId, b.EntityId, a.Name, b.Name, interaction, pair.Games));
                }
            }
        }

        return result;
    }
}
