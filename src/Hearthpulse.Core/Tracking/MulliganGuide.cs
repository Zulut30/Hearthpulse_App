using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// HSReplay mulligan numbers for one card of an archetype (rates as fractions, 0..1).
/// </summary>
/// <param name="Offered">Times the card was offered in the starting cards.</param>
/// <param name="InOpeningHand">Games the card was in hand after the mulligan, kept or drawn as a replacement.</param>
/// <param name="OpeningHandWinRate">Win rate of those games (HSReplay's "Mulligan WR").</param>
/// <param name="AvgTurnPlayed">The deck's average turn for playing the card; null when unknown (Firestone data).</param>
public sealed record GuideCard(string CardId, int DbfId, int Cost, int Offered, double KeepRate, int InOpeningHand, double? OpeningHandWinRate, double? AvgTurnPlayed)
{
    /// <summary>Games the card was drawn in (any turn, the opening hand included) and their win rate; HSReplay only.</summary>
    public int Drawn { get; init; }

    public double? DrawnWinRate { get; init; }
}

/// <summary>
/// HSGuru's numbers for one card in one mulligan slice (<c>/v1/hsguru/mulligan</c>). Impacts are win rate points
/// against the deck's own win rate in the same slice, as fractions (0.042 = +4.2 points).
/// </summary>
/// <param name="Offered">Games the card was offered in the mulligan.</param>
/// <param name="OfferedImpact">Win rate of those games against the deck's: independent of the player's choice.</param>
/// <param name="Kept">Games the offered card was kept.</param>
/// <param name="Drawn">Games the card was drawn (any turn).</param>
public sealed record SliceCard(string CardId, int DbfId, int Offered, double? OfferedImpact, int Kept, double? KeptImpact, int Drawn, double? DrawnImpact)
{
    public int Replaced => Math.Max(0, Offered - Kept);

    public double? KeepRate => Offered > 0 ? (double)Kept / Offered : null;

    /// <summary>Win rate of the games where the offered card went back, from offered = kept + replaced.</summary>
    public double? ReplacedImpact => OfferedImpact is { } offered && KeptImpact is { } kept && Replaced > 0
        ? (offered * Offered - kept * Kept) / Replaced
        : null;
}

/// <summary>One HSGuru mulligan slice of an archetype: a rank, the coin and the opponent's class ("any" for all).</summary>
/// <param name="Rank">"legend", "all", ... (HSGuru's keys).</param>
/// <param name="Coin"><see cref="MulliganGuide.AnyCoin"/>, "going_first" or "on_coin".</param>
/// <param name="OpponentClass"><see cref="MulliganGuide.AnyOpponent"/> or a class ("WARLOCK").</param>
public sealed record MulliganSlice(string Rank, string Coin, string OpponentClass, int Games, IReadOnlyList<SliceCard> Cards)
{
    private IReadOnlyDictionary<string, SliceCard>? _byId;

    /// <summary>The card's row; a slice has hundreds and the guide asks for every card of the deck on each update.</summary>
    public SliceCard? Card(string cardId) =>
        (_byId ??= Cards.GroupBy(c => c.CardId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal))
        .GetValueOrDefault(cardId);
}

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

    /// <summary>
    /// The deck's win rate over the same games as the card numbers, when the source publishes it (Firestone does);
    /// otherwise <see cref="MulliganGuide.Baseline"/> estimates it from the cards.
    /// </summary>
    public double? BaselineWinRate { get; init; }

    /// <summary>The HSGuru archetype the deck matches, whose mulligan slices are in <see cref="Slices"/>; null when none.</summary>
    public string? HsguruArchetype { get; init; }

    /// <summary>HSGuru mulligan slices loaded so far (all games, and the coin and the opponent's class once known).</summary>
    public IReadOnlyList<MulliganSlice> Slices { get; init; } = [];
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
/// with the card in the opening hand minus the deck's baseline. A hand adds up the deltas of the kept cards and one
/// replacement delta per replaced card, shrunk by the square root of the hand size because card effects overlap.
/// Duplicates and the curve use labelled heuristics because the data has no per-copy or per-turn split; synergies need
/// pair data no endpoint serves yet.
/// </summary>
public static class MulliganGuide
{
    /// <summary>Fewer games than this and a card's numbers are too noisy to use.</summary>
    public const int MinSample = 30;

    /// <summary>A deck must share at least this much of its cards with one of an archetype's lists to count as that archetype.</summary>
    public const double MinDeckOverlap = 0.5;

    /// <summary>A deck must reach this <see cref="Presence"/> in an archetype's mulligan rows to count as that archetype.</summary>
    public const double MinPresence = 0.4;

    /// <summary>Class matchups with fewer games leave the baseline alone.</summary>
    public const int MinClassGames = 200;

    public const int MinPairGames = 200;

    /// <summary>Pair interactions smaller than this (in win rate, 0.015 = 1.5 points) are noise.</summary>
    public const double MinInteraction = 0.015;

    /// <summary>Cards costing this much or less, or usually played by <see cref="EarlyTurn"/>, are early plays.</summary>
    public const int EarlyCost = 2;

    public const double EarlyTurn = 3.0;

    /// <summary>Cards costing this much or more, or costing 4 and usually played on <see cref="LateTurn"/> or later, are late.</summary>
    public const int LateCost = 5;

    public const double LateTurn = 6.0;

    /// <summary>
    /// A card whose <see cref="MulliganCardStat.Edge"/> is smaller than this (half a win rate point) either way is a close
    /// call: the overlay draws it neutral. The call itself follows the sign, so a card worse than its replacement is never
    /// "Keep" (it used to be, inside this margin: a −0.6 card next to a −0.7 replacement was kept).
    /// </summary>
    public const double CloseCall = 0.005;

    /// <summary>HSGuru's slice keys for "every game".</summary>
    public const string AnyCoin = "any_player";

    public const string AnyOpponent = "any";

    /// <summary>A slice with fewer games is not used (HSGuru keeps class slices from about 400 games at Legend).</summary>
    public const int MinSliceGames = 2000;

    /// <summary>A card offered (or kept, replaced, drawn) fewer times than this in a slice has no numbers there.</summary>
    public const int MinSliceCount = 200;

    /// <summary>HSGuru ranks the guide reads, most wanted first: Legend like the HSReplay numbers, else all ranks.</summary>
    public static readonly IReadOnlyList<string> SliceRanks = ["legend", "all"];

    /// <summary>
    /// The archetype a deck belongs to. Two signals, because HSReplay lists no decks for some archetypes: the share of
    /// the deck found in the archetype's closest list (needs <see cref="MinDeckOverlap"/>), and the deck's
    /// <see cref="Presence"/> in the archetype's mulligan rows (needs <see cref="MinPresence"/>). The archetype that
    /// clears its bar by the widest margin wins; ties go to the one with more games.
    /// </summary>
    /// <returns>The archetype and its score (1 = exactly at a bar), or null when none clears a bar.</returns>
    public static (MetaArchetype Archetype, double Score)? MatchArchetype(IReadOnlyDictionary<int, int> deck, IEnumerable<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks, IReadOnlyList<GuideCard> Cards)> candidates)
    {
        (MetaArchetype Archetype, double Score)? best = null;
        foreach (var (archetype, decks, cards) in candidates)
        {
            var score = Math.Max(Overlap(deck, decks) / MinDeckOverlap, Presence(deck, cards) / MinPresence);
            if (score >= 1 && (best is null || score > best.Value.Score || (score == best.Value.Score && archetype.Games > best.Value.Archetype.Games)))
            {
                best = (archetype, score);
            }
        }

        return best;
    }

    /// <summary>
    /// How much of the deck the archetype plays: each card counts by how often the archetype was offered it, relative
    /// to its ten most offered cards (capped at 1). Measured on HSReplay's own lists: about 0.55-0.6 for a list of
    /// the archetype, 0.1-0.2 for a list of another archetype of the class.
    /// </summary>
    public static double Presence(IReadOnlyDictionary<int, int> deck, IReadOnlyList<GuideCard> cards)
    {
        var offered = new Dictionary<int, int>();
        foreach (var c in cards.Where(c => c.DbfId > 0))
        {
            offered[c.DbfId] = Math.Max(offered.GetValueOrDefault(c.DbfId), c.Offered);
        }

        var top = offered.Values.OrderDescending().Take(10).DefaultIfEmpty(0).Average();
        if (top <= 0)
        {
            return 0;
        }

        return deck.Sum(c => c.Value * Math.Min(1, offered.GetValueOrDefault(c.Key) / top)) / Math.Max(1, deck.Values.Sum());
    }

    /// <summary>Share of the deck's cards (copies counted) found in the closest of <paramref name="decks"/>.</summary>
    public static double Overlap(IReadOnlyDictionary<int, int> deck, IReadOnlyList<GuideDeck> decks)
    {
        var size = Math.Max(1, deck.Values.Sum());
        return decks.Count == 0 ? 0 : decks.Max(d => deck.Sum(c => Math.Min(c.Value, d.Cards.GetValueOrDefault(c.Key)))) / (double)size;
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
        if (hand.Count == 0 || (data.BaselineWinRate ?? Baseline(data.Cards)) is not { } baseline)
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
        var coin = goingFirst switch { true => "going_first", false => "on_coin", null => null };
        var slices = data.Slices;

        // HSGuru's slices move a card by how much better or worse it does on the coin / against this class than in all
        // games (offered win rates, which do not depend on the player's choice).
        double? CoinShift(string cardId) => coin is null ? null : Shift(slices, cardId, coin, AnyOpponent);
        double? OpponentShift(string cardId) => opponentClass is not { Length: > 0 } ? null : Shift(slices, cardId, AnyCoin, opponentClass.ToUpperInvariant());

        double? DeltaOf(string cardId) =>
            stats.TryGetValue(cardId, out var s) && s.Offered >= MinSample && s.InOpeningHand >= MinSample && s.OpeningHandWinRate is { } rate
                ? rate - baseline + (CoinShift(cardId) ?? 0) + (OpponentShift(cardId) ?? 0)
                : null;

        // Pair win rates are over all games: their interaction is measured against the cards' unshifted deltas, or a
        // coin or class shift of both cards would show up as a synergy of the same size (and cancel itself in the sum).
        double? AllGamesDeltaOf(string cardId) =>
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
            // The most specific slice that has the card: its keep rate and kept / replaced / drawn numbers fit this game.
            var shown = Fitting(slices, coin, opponentClass)
                .Select(slice => (Slice: slice, Card: slice.Card(card.CardId)))
                .FirstOrDefault(x => x.Card is { Offered: >= MinSliceCount, OfferedImpact: not null });
            var sliceCard = shown.Card;
            rows.Add(new MulliganCardStat(card.EntityId, card.CardId, card.Name,
                sliceCard?.KeepRate ?? (s is { Offered: >= MinSample } ? s.KeepRate : null),
                delta is null ? null : s!.OpeningHandWinRate,
                delta,
                s?.Offered ?? 0)
            {
                Copy = copy,
                Cost = card.Cost,
                ExpectedTurn = turn,
                CoinShift = delta is null ? null : CoinShift(card.CardId),
                OpponentShift = delta is null ? null : OpponentShift(card.CardId),
                KeptDelta = sliceCard is { Kept: >= MinSliceCount } ? sliceCard.KeptImpact : null,
                ReplacedDelta = sliceCard is { Replaced: >= MinSliceCount } ? sliceCard.ReplacedImpact : null,
                DrawnDelta = sliceCard is { Drawn: >= MinSliceCount, DrawnImpact: { } drawn } ? drawn
                    : s is { Drawn: >= MinSample, DrawnWinRate: { } drawnRate } ? drawnRate - baseline
                    : null,
                Slice = shown.Slice,
            });
        }

        var synergies = Synergies(data.Pairs, hand, baseline, AllGamesDeltaOf);
        var view = new MulliganView(context, baseRate, rows, data.Source)
        {
            ReplaceDelta = replace,
            CardBaseline = baseline,
            OpponentClass = classShift is not null || rows.Exists(r => r.OpponentShift is not null) ? opponentClass : null,
            ClassShift = classShift,
            GoingFirst = initiativeShift is not null || rows.Exists(r => r.CoinShift is not null) ? goingFirst : null,
            HasPairData = data.Pairs.Count > 0,
            Synergies = synergies,
            SliceRank = rows.Select(r => r.Slice?.Rank).OfType<string>().GroupBy(r => r).MaxBy(g => g.Count())?.Key,
        };

        var recommended = Recommend(view);
        var keep = recommended.ToHashSet();
        return view with
        {
            Recommended = recommended,
            // A second copy is called by the duplicate heuristic, not by a number: it shows none.
            Cards = rows.Select(r => r with
            {
                Call = Call(r, recommended),
                Edge = r is { Copy: 1, Delta: not null } ? Toggle(view, keep, r.EntityId) : null,
            }).ToList(),
        };
    }

    /// <summary>The estimate and warnings for keeping exactly <paramref name="kept"/> (entity ids of offered cards).</summary>
    public static MulliganImpact Evaluate(MulliganView view, IReadOnlyCollection<int> kept)
    {
        var keptSet = kept.ToHashSet();
        var keptCards = view.Cards.Where(c => keptSet.Contains(c.EntityId)).ToList();
        var warnings = new List<string>();
        if (keptCards.Count > 0 && !keptCards.Exists(IsEarly))
        {
            warnings.Add(Loc.F("Mulligan.Warn.NoEarly", EarlyCost));
        }

        var late = keptCards.Count(IsLate);
        if (late >= 2)
        {
            warnings.Add(Loc.P("Mulligan.Warn.TopHeavy", late, LateCost, LateTurn));
        }

        foreach (var pair in keptCards.GroupBy(c => c.CardId).Where(g => g.Count() > 1 && !IsEarly(g.First())))
        {
            warnings.Add(Loc.F("Mulligan.Warn.BothCopies", pair.First().Name));
        }

        return new MulliganImpact(
            Expected(view, keptSet),
            Expected(view, view.Recommended.ToHashSet()),
            Expected(view, new HashSet<int>()),
            keptCards.Count,
            warnings,
            view.Synergies.Where(s => keptSet.Contains(s.EntityA) && keptSet.Contains(s.EntityB)).ToList());
    }

    /// <summary>
    /// Estimated win rate when exactly <paramref name="kept"/> stay in hand and the rest are replaced: the baseline
    /// plus the hand's summed deltas divided by √(hand size). The deltas are marginal (each measured with whatever else
    /// was in hand), so a plain sum counts shared causes several times; the √ shrink is a heuristic for that overlap.
    /// </summary>
    public static double Expected(MulliganView view, IReadOnlySet<int> kept) =>
        Math.Clamp(view.BaseWinRate + Sum(view, kept) / Math.Sqrt(Math.Max(1, view.Cards.Count)), 0, 1);

    /// <summary>A card is an early play when it is cheap or usually played by turn 3.</summary>
    public static bool IsEarly(MulliganCardStat card) => card.Cost <= EarlyCost || card.ExpectedTurn <= EarlyTurn;

    /// <summary>A card is late when it is expensive, or costs 4+ and is usually held until turn 6.</summary>
    public static bool IsLate(MulliganCardStat card) => card.Cost >= LateCost || (card.Cost >= 4 && card.ExpectedTurn >= LateTurn);

    /// <summary>
    /// What keeping the card adds to the recommended keeps: the summed deltas with it kept minus with it replaced, the
    /// other cards as recommended. For a plain card that is its delta minus the replacement's; a kept synergy partner or a
    /// kept second copy of the same card count too. The recommendation is the best sum, so the sign always matches the call.
    /// </summary>
    private static double Toggle(MulliganView view, IReadOnlySet<int> keep, int entityId)
    {
        var with = new HashSet<int>(keep) { entityId };
        var without = new HashSet<int>(keep);
        without.Remove(entityId);
        return Sum(view, with) - Sum(view, without);
    }

    /// <summary>The hand's summed deltas: first kept copies, one replacement per replaced card, kept pair interactions.</summary>
    private static double Sum(MulliganView view, IReadOnlySet<int> kept)
    {
        var total = 0.0;
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

        return total + view.Synergies.Where(s => kept.Contains(s.EntityA) && kept.Contains(s.EntityB)).Sum(s => s.Interaction);
    }

    /// <summary>
    /// The slices that fit a game, most specific first: against the opponent's class, on the coin / going first, then all
    /// games; each at Legend before all ranks. Thin slices (<see cref="MinSliceGames"/>) are left out.
    /// </summary>
    public static IEnumerable<MulliganSlice> Fitting(IReadOnlyList<MulliganSlice> slices, string? coin, string? opponentClass)
    {
        var keys = new List<(string Coin, string Opponent)>();
        if (opponentClass is { Length: > 0 })
        {
            keys.Add((AnyCoin, opponentClass.ToUpperInvariant()));
        }

        if (coin is not null)
        {
            keys.Add((coin, AnyOpponent));
        }

        keys.Add((AnyCoin, AnyOpponent));
        foreach (var (c, o) in keys)
        {
            foreach (var rank in SliceRanks)
            {
                if (Find(slices, rank, c, o) is { } slice)
                {
                    yield return slice;
                }
            }
        }
    }

    /// <summary>
    /// How much better (+) or worse the card is offered in the <paramref name="coin"/> / <paramref name="opponent"/> slice
    /// than in all games of the same rank: the difference of its two offered impacts. Null without both numbers.
    /// </summary>
    private static double? Shift(IReadOnlyList<MulliganSlice> slices, string cardId, string coin, string opponent)
    {
        foreach (var rank in SliceRanks)
        {
            if (Find(slices, rank, coin, opponent)?.Card(cardId) is { Offered: >= MinSliceCount, OfferedImpact: { } there }
                && Find(slices, rank, AnyCoin, AnyOpponent)?.Card(cardId) is { Offered: >= MinSliceCount, OfferedImpact: { } all })
            {
                return there - all;
            }
        }

        return null;
    }

    private static MulliganSlice? Find(IReadOnlyList<MulliganSlice> slices, string rank, string coin, string opponent) =>
        slices.FirstOrDefault(s => s.Games >= MinSliceGames && s.Rank == rank && s.Coin == coin && string.Equals(s.OpponentClass, opponent, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The best keeps by <see cref="Expected"/> (every subset; a hand has at most five cards). A card is kept when it beats
    /// its replacement, ties included; there is no margin in favour of keeping. Second copies are fixed beforehand by the
    /// duplicate heuristic: keep one only when it is an early play most players keep. Cards without data count 0, like
    /// an average card.
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
                if ((mask & (1 << i)) != 0)
                {
                    set.Add(free[i].EntityId);
                }
            }

            var value = Sum(view, set);
            if (best is null || value > bestValue + 1e-9 || (Math.Abs(value - bestValue) <= 1e-9 && set.Count > best.Count))
            {
                best = set;
                bestValue = value;
            }
        }

        return cards.Where(c => best!.Contains(c.EntityId)).Select(c => c.EntityId).ToList();
    }

    private static MulliganCall Call(MulliganCardStat card, IReadOnlyList<int> recommended) =>
        card.Delta is null ? MulliganCall.NoData
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
                if (Math.Abs(interaction) >= MinInteraction && !result.Exists(s => (s.EntityA == a.EntityId || s.EntityB == a.EntityId) && (s.EntityA == b.EntityId || s.EntityB == b.EntityId)))
                {
                    result.Add(new MulliganSynergy(a.EntityId, b.EntityId, a.Name, b.Name, interaction, pair.Games));
                }
            }
        }

        return result;
    }
}
