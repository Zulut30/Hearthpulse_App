namespace Hearthpulse.Core.Arena;

/// <summary>
/// What an Arena run gave, as the player entered it (<c>docs/arena-rewards.md</c>): no log the client writes names the
/// rewards, so they are typed in on the run's row. Each value is 0 or more.
/// </summary>
public sealed record ArenaRewards(int Gold, int Dust, int Packs, int Tickets, int Cards)
{
    /// <summary>The most a field takes: more than any run gives, so a typo stands out.</summary>
    public const int MaxValue = 99_999;

    public static ArenaRewards None { get; } = new(0, 0, 0, 0, 0);

    public bool IsEmpty => Gold == 0 && Dust == 0 && Packs == 0 && Tickets == 0 && Cards == 0;

    /// <summary>Every field within 0..<see cref="MaxValue"/>.</summary>
    public bool IsValid => new[] { Gold, Dust, Packs, Tickets, Cards }.All(v => v is >= 0 and <= MaxValue);

    public static ArenaRewards operator +(ArenaRewards a, ArenaRewards b) =>
        new(a.Gold + b.Gold, a.Dust + b.Dust, a.Packs + b.Packs, a.Tickets + b.Tickets, a.Cards + b.Cards);

    /// <summary>The sum of the runs' rewards (none for no runs).</summary>
    public static ArenaRewards Sum(IEnumerable<ArenaRewards> rewards) => rewards.Aggregate(None, (sum, r) => sum + r);
}
