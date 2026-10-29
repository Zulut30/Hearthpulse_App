namespace Hearthpulse.Core.Replay;

/// <summary>
/// The game a format 4 replay is of: enough for a library card and a match page without a history row. Never holds
/// names, BattleTags, account ids or the deck name.
/// </summary>
public sealed record ReplaySummary
{
    public string Mode { get; init; } = "Other";

    public string GameType { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    /// <summary>UTC.</summary>
    public DateTime StartedAt { get; init; }

    public int Seconds { get; init; }

    public string HeroCardId { get; init; } = string.Empty;

    public string PlayerClass { get; init; } = string.Empty;

    public string? OpponentHeroCardId { get; init; }

    public string? OpponentClass { get; init; }

    /// <summary>As <c>GameRecord.Result</c>: Win, Loss, Tie, Unknown, Abandoned.</summary>
    public string Result { get; init; } = "Unknown";

    public int Turns { get; init; }

    public bool? WentFirst { get; init; }

    public string? DeckCode { get; init; }

    public string? OpponentArchetype { get; init; }

    public int Placement { get; init; }

    public int? RatingBefore { get; init; }

    public int? RatingAfter { get; init; }

    public int TavernTier { get; init; }

    public string? AnomalyCardId { get; init; }

    public IReadOnlyList<string> Tribes { get; init; } = [];

    /// <summary>Battlegrounds: hero power card ids at the end of the game.</summary>
    public IReadOnlyList<string> HeroPowers { get; init; } = [];

    /// <summary>Battlegrounds: trinkets, quest and reward card ids at the end of the game.</summary>
    public IReadOnlyList<string> Trinkets { get; init; } = [];

    public IReadOnlyList<ReplayBgMinion> FinalBoard { get; init; } = [];

    public IReadOnlyList<string> FinalHand { get; init; } = [];
}

/// <summary>A minion of the final board: stats, golden, keywords (TAUNT, DIVINE_SHIELD, REBORN, VENOMOUS...).</summary>
public sealed record ReplayBgMinion(string CardId, int Attack, int Health, bool Golden, IReadOnlyList<string> Keywords);
