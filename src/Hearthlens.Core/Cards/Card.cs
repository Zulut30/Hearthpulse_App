namespace Hearthlens.Core.Cards;

public sealed class Card
{
    public required string Id { get; init; }

    public int DbfId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public int Cost { get; init; }

    public int Attack { get; init; }

    public int Health { get; init; }

    public int Armor { get; init; }

    public string Type { get; init; } = string.Empty;

    public string Rarity { get; init; } = string.Empty;

    public string CardClass { get; init; } = string.Empty;

    public string Set { get; init; } = string.Empty;

    public IReadOnlyList<string> Races { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Mechanics { get; init; } = Array.Empty<string>();

    public bool Collectible { get; init; }

    public int TechLevel { get; init; }

    public bool IsBattlegroundsPoolMinion { get; init; }

    public bool IsBattlegroundsPoolSpell { get; init; }

    public bool IsBattlegroundsHero { get; init; }

    public bool IsBattlegroundsDuosExclusive { get; init; }

    public int BattlegroundsPremiumDbfId { get; init; }

    public int BattlegroundsNormalDbfId { get; init; }

    public int BattlegroundsSkinParentId { get; init; }

    public bool IsLegendary => Rarity == "LEGENDARY";

    public bool IsBattlegroundsCard => Set == "BATTLEGROUNDS" || TechLevel > 0 || IsBattlegroundsHero;

    public bool HasRace(string race) => Races.Contains(race) || Races.Contains("ALL");

    public override string ToString() => $"{Name} ({Id})";
}
