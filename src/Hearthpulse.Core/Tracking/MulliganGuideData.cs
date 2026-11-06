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

    /// <summary>The mulligan rank filter the guide was built for (<see cref="MulliganGuide.Ranks"/>); its slices follow it.</summary>
    public string Rank { get; init; } = MulliganGuide.DefaultRank;
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
