using System.Globalization;
using Hearthpulse.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed record GameRecord
{
    public long Id { get; init; }

    public required string Key { get; init; }

    public DateTime StartedAt { get; init; }

    public DateTime EndedAt { get; init; }

    public string Mode { get; init; } = "Other";

    public string GameType { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string OpponentName { get; init; } = string.Empty;

    public string HeroCardId { get; init; } = string.Empty;

    public string OpponentHeroCardId { get; init; } = string.Empty;

    public string PlayerClass { get; init; } = string.Empty;

    public string OpponentClass { get; init; } = string.Empty;

    /// <summary>
    /// <c>Result</c> of a game the client left before it ended (closed or crashed mid-game): its outcome is not known, so
    /// it is kept in History but left out of win rates, placements and the other stats.
    /// </summary>
    public const string AbandonedResult = "Abandoned";

    /// <summary>Win / Loss / Tie / Unknown, or <see cref="AbandonedResult"/>.</summary>
    public string Result { get; init; } = "Unknown";

    /// <summary>The client left the game before it ended (see <see cref="AbandonedResult"/>).</summary>
    public bool IsAbandoned => Result == AbandonedResult;

    public int Placement { get; init; }

    public int? RatingBefore { get; init; }

    public int? RatingAfter { get; init; }

    public int Turns { get; init; }

    public string? DeckName { get; init; }

    public string? DeckCode { get; init; }

    public int TavernTier { get; init; }

    public string? FinalBoard { get; init; }

    public string? Races { get; init; }

    public string? Rank { get; init; }

    public string Source { get; init; } = "live";

    // ---- My Stats fields (added by migration; null in rows saved before them) ----

    /// <summary>
    /// Battlegrounds: every combat of the game as JSON, oldest first:
    /// <c>[{"turn":3,"result":"Win","opponent":"TB_BaconShop_HERO_08","taken":0,"dealt":5}]</c>.
    /// <c>result</c> is Win/Loss/Tie from the player's side; <c>taken</c>/<c>dealt</c> is hero damage after the fight.
    /// </summary>
    public string? CombatResults { get; init; }

    /// <summary>
    /// Battlegrounds: the simulator's prediction for each combat of <see cref="CombatResults"/>, same order, null where
    /// nothing was simulated:
    /// <c>[{"turn":3,"win":0.62,"tie":0.08,"loss":0.3,"runs":2000,"cap":5,"taken":{"mean":4.2,"low":3,"high":5,"max":7},
    /// "dealt":{...},"lethal":0,"kill":0.1}]</c>. <c>taken</c>/<c>dealt</c> is hero damage in the fights lost/won (80 %
    /// interval <c>low</c>..<c>high</c>), <c>lethal</c>/<c>kill</c> the share of fights that kill our/their hero.
    /// </summary>
    public string? CombatOdds { get; init; }

    /// <summary>Battlegrounds: hero card ids offered at the hero pick, comma-separated, in offer order.</summary>
    public string? OfferedHeroes { get; init; }

    /// <summary>Battlegrounds: card id of the lobby's anomaly, if the game had one.</summary>
    public string? AnomalyCardId { get; init; }

    /// <summary>Constructed: ranked star level 1-50 (Bronze 10 = 1 ... Diamond 1 = 50, 51+ = Legend) at game start.</summary>
    public int? RankStarLevel { get; init; }

    /// <summary>Constructed: Legend rank number at game start (null below Legend or when unknown).</summary>
    public int? RankLegend { get; init; }

    /// <summary>Constructed and Arena: true when the player went first, false when on the coin.</summary>
    public bool? WentFirst { get; init; }

    /// <summary>Deck id from Decks.log (Constructed decks and Arena runs: one Arena run has one deck id).</summary>
    public long? DeckId { get; init; }

    /// <summary>The log's <c>ScenarioID</c>: tells one week's Tavern Brawl from the next (<c>docs/brawl.md</c>).</summary>
    public int? ScenarioId { get; init; }

    // ---- Deck page fields (Constructed; null in rows saved before them) ----

    /// <summary>Local mulligan as JSON (<see cref="GameCards.MulliganJson"/>): offered, replaced and received card ids.</summary>
    public string? Mulligan { get; init; }

    /// <summary>Deck cards the player drew, JSON <c>[{"id":..,"turn":..}]</c>; turn 0 is the opening hand after the mulligan.</summary>
    public string? CardsDrawn { get; init; }

    /// <summary>Deck cards the player played from hand, JSON <c>[{"id":..,"turn":..}]</c> (the player's own turn number).</summary>
    public string? CardsPlayed { get; init; }

    /// <summary>The opponent's starting-deck cards it played face up, JSON <c>[{"id":..,"turn":..}]</c>. Nothing hidden.</summary>
    public string? OpponentCards { get; init; }

    /// <summary>The opponent's archetype when its played cards placed it clearly (<c>ArchetypeGuess</c>), else null.</summary>
    public string? OpponentArchetype { get; init; }

    /// <summary>File name of the game's match replay in the replays folder (<c>Replay/ReplayStore</c>); null when none was recorded.</summary>
    public string? Replay { get; init; }

    public int? RatingDelta => RatingBefore is { } b && RatingAfter is { } a ? a - b : null;

    public TimeSpan Duration => EndedAt - StartedAt;

    public bool IsBattlegrounds => Mode is "Battlegrounds" or "Duos";

    /// <summary>Brought over from HDT or Firestone (<c>docs/migration.md</c>): never sent to the profile or the stats pool.</summary>
    public bool FromOtherTracker => Source is Migration.HdtImport.Source or Migration.FirestoneImport.Source;
}
