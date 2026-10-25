#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>An alternative archetype of the opponent; <see cref="Fraction"/> is its chance for the bar.</summary>
public sealed record ArchetypeRow(string Name, string Chance, double Fraction);

/// <summary>A card the opponent's archetype usually runs and it has not shown yet.</summary>
public sealed record LikelyTile(string CardId, string Name, int Cost, string Chance);

/// <summary>
/// One player's turn timer: while its turn runs, the time spent and the limit (or the time left once the rope burns);
/// otherwise its last turn. <see cref="Average"/> is its average turn this game.
/// </summary>
public sealed record TurnTimerSide(string Label, bool Active, bool Rope, string Time, string Detail, string Average)
{
    public static TurnTimerSide Idle { get; } = new(string.Empty, false, false, "—", string.Empty, string.Empty);
}
