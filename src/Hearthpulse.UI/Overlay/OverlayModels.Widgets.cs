namespace Hearthpulse.UI.Overlay;

/// <summary>An alternative archetype of the opponent; <see cref="Fraction"/> is its chance for the bar.</summary>
public sealed record ArchetypeRow(string Name, string Chance, double Fraction);

/// <summary>A card the opponent's archetype usually runs and it has not shown yet.</summary>
public sealed record LikelyTile(string CardId, string Name, int Cost, string Chance);
