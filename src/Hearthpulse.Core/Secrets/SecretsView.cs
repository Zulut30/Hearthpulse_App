namespace Hearthpulse.Core.Secrets;

/// <summary>The opponent's active Secrets (<see cref="Slots"/>) and every Secret they can be (<see cref="Rows"/>).</summary>
/// <param name="Rows">Possible Secrets first, then the ones crossed out by hand, then the ones ruled out.</param>
public sealed record SecretsView(IReadOnlyList<SecretSlot> Slots, IReadOnlyList<SecretRow> Rows)
{
    /// <summary>"Hunter · Standard": the pool the rows come from.</summary>
    public string Pool { get; init; } = string.Empty;
}

/// <summary>One active opponent Secret, with only what the player could see about it.</summary>
/// <param name="CardClass">The Secret's class (the colour of its icon).</param>
/// <param name="Turn">The opponent's turn it was put into play.</param>
/// <param name="PaidCost">Mana they spent on it, when it was played from hand; null otherwise.</param>
/// <param name="CreatedBy">Name of the visible card that made it; null for one from the deck or an unseen source.</param>
/// <param name="KnownCardId">The card, when the player saw it before it was played (it was revealed in hand).</param>
/// <param name="Possible">How many Secrets of its pool it can still be.</param>
public sealed record SecretSlot(int EntityId, string CardClass, int Turn, int? PaidCost, string? CreatedBy, string? KnownCardId, int Possible);

/// <summary>One Secret of the pool.</summary>
/// <param name="Key">Reprint-free card id; the key for crossing it out by hand.</param>
/// <param name="Possible">How many active Secrets can still be this one (0 = ruled out by the game's events).</param>
/// <param name="Crossed">The player crossed it out on the overlay.</param>
/// <param name="Known">One of the active Secrets is known to be this card.</param>
public sealed record SecretRow(string Key, string CardId, string Name, int Cost, string CardClass, int Possible, bool Crossed, bool Known)
{
    /// <summary>Still a candidate: not ruled out and not crossed out.</summary>
    public bool IsOpen => (Possible > 0 || Known) && !Crossed;
}
