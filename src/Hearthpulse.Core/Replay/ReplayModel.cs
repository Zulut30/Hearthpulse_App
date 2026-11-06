using System.Globalization;

namespace Hearthpulse.Core.Replay;

public enum ReplayPlayer
{
    Local,
    Opponent,
}

/// <summary>A card on the table at one step. <see cref="CardId"/> is null for a card back.</summary>
public sealed record ReplayCard(int Id, ReplayPlayer Player, string Zone, int Position, string? CardId, string Type)
{
    public int Attack { get; init; }

    /// <summary>Health left (HEALTH minus DAMAGE; durability for a weapon).</summary>
    public int Health { get; init; }

    public bool Damaged { get; init; }

    public int Armor { get; init; }

    public int Cost { get; init; }

    public bool Golden { get; init; }

    /// <summary>Keyword tags it has now (TAUNT, DIVINE_SHIELD, FROZEN...).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>How a card back got into the opponent's hand: start, drawn, created, returned, stolen, coin.</summary>
    public string? Origin { get; init; }

    /// <summary>The player turn it got into its zone (0 = opening hand / mulligan).</summary>
    public int EnteredTurn { get; init; }

    /// <summary>
    /// For a card back: what the log revealed about this card by the end of the game (post-game part of the file), or
    /// null. The card was still hidden at this step; only the match page of a finished game shows it.
    /// </summary>
    public ReplayReveal? Reveal { get; init; }

    public bool IsCardBack => CardId is null;
}

/// <summary>
/// What the log revealed by the end of the game about one opponent card that was hidden at some point (in hand, as a
/// secret, in the deck): the first card id it was given and the player turn of that, how it came to light (played,
/// summoned, burned, shown), whether it was created during the game rather than part of the deck, and the card it was
/// transformed from (dbf id, 0 = none) when the game said so.
/// </summary>
public sealed record ReplayReveal(int EntityId, string CardId, int Turn, string How, bool Created, int TransformedFrom = 0);

/// <summary>
/// The opponent's mulligan as the log revealed it by the end of the game: each card is its id, or null when it was
/// never revealed.
/// </summary>
public sealed record ReplayOpponentMulligan(IReadOnlyList<string?> Kept, IReadOnlyList<string?> Replaced, IReadOnlyList<string?> Received);

/// <summary>One side of the table at one step.</summary>
public sealed record ReplayBoard
{
    public ReplayCard? Hero { get; init; }

    public ReplayCard? HeroPower { get; init; }

    public ReplayCard? Weapon { get; init; }

    /// <summary>Minions and locations in play, left to right.</summary>
    public IReadOnlyList<ReplayCard> Minions { get; init; } = [];

    /// <summary>The hand, left to right; the opponent's cards are card backs.</summary>
    public IReadOnlyList<ReplayCard> Hand { get; init; } = [];

    /// <summary>Secrets and quests; an opponent's secret is a card back.</summary>
    public IReadOnlyList<ReplayCard> Secrets { get; init; } = [];

    public int DeckCount { get; init; }

    /// <summary>Mana crystals this turn (temporary ones included).</summary>
    public int Mana { get; init; }

    public int ManaUsed { get; init; }

    public int OverloadLocked { get; init; }

    public int Health => Hero?.Health ?? 0;

    public int Armor => Hero?.Armor ?? 0;

    /// <summary>Total attack and health of the minions in play.</summary>
    public int BoardAttack => Minions.Where(m => m.Type == "MINION").Sum(m => m.Attack);

    public int BoardHealth => Minions.Where(m => m.Type == "MINION").Sum(m => m.Health);
}

public enum ReplayActionKind
{
    Start,
    Mulligan,
    TurnStart,
    Play,
    HeroPower,

    /// <summary>A card on the table used (a location).</summary>
    Use,
    Attack,
    Choice,
    End,
}

/// <summary>A card that left the board during an action (died, destroyed, weapon broke).</summary>
public sealed record ReplayDeath(ReplayPlayer Player, string CardId, string Type)
{
    /// <summary>The replay id of the card that died (the table marks it where it stood).</summary>
    public int Ref { get; init; }
}

/// <summary>What happened in one step.</summary>
public sealed record ReplayAction(ReplayActionKind Kind, ReplayPlayer? Player)
{
    /// <summary>The card played, the attacker, or the card that offered the choice.</summary>
    public string? CardId { get; init; }

    public string? TargetId { get; init; }

    /// <summary>The replay id of the attacker or the card played (plan R2: the table marks it).</summary>
    public int? ActorRef { get; init; }

    /// <summary>The replay id of the attack's target, or of the card or hero a play aimed at; null without one shown.</summary>
    public int? TargetRef { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public IReadOnlyList<string> Picked { get; init; } = [];

    public IReadOnlyList<ReplayDeath> Deaths { get; init; } = [];

    /// <summary>The local result for <see cref="ReplayActionKind.End"/>: WON, LOST, TIED, CONCEDED.</summary>
    public string? Result { get; init; }
}

/// <summary>The table after one action resolved.</summary>
public sealed record ReplayFrame(int Index, int Turn, ReplayPlayer? Active, bool Mulligan, ReplayAction Action, ReplayBoard Local, ReplayBoard Opponent)
{
    /// <summary>The acting player's own turn number (game turns 2N-1 and 2N are turn N); 0 during the mulligan.</summary>
    public int PlayerTurn => Mulligan ? 0 : (Turn + 1) / 2;

    public ReplayBoard Of(ReplayPlayer player) => player == ReplayPlayer.Local ? Local : Opponent;
}

/// <summary>One game turn (or the mulligan, <see cref="Turn"/> 0) and its frames.</summary>
public sealed record ReplayTurn(int Turn, ReplayPlayer? Player, int FirstFrame, int LastFrame)
{
    public int PlayerTurn => (Turn + 1) / 2;

    /// <summary>Seconds the turn took, when the next turn's start is known.</summary>
    public int? Seconds { get; init; }
}

/// <summary>The local mulligan: the cards offered, kept and replaced, and what came instead.</summary>
public sealed record ReplayMulligan(IReadOnlyList<string> Offered, IReadOnlyList<string> Kept, IReadOnlyList<string> Replaced, IReadOnlyList<string> Received);

/// <summary>A card a player showed: played from hand, first on <see cref="Turn"/> (player turn), <see cref="Count"/> times.</summary>
public sealed record ShownCard(string CardId, int Turn, int Count);
