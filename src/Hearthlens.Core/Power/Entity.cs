namespace Hearthlens.Core.Power;

/// <summary>An entity from the Power.log stream: a card, hero, player or the game itself.</summary>
public sealed class Entity
{
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

    public Entity(int id)
    {
        Id = id;
    }

    public int Id { get; }

    public string CardId { get; internal set; } = string.Empty;

    /// <summary>Localised name as printed by the client, when known.</summary>
    public string Name { get; internal set; } = string.Empty;

    public IReadOnlyDictionary<string, string> Tags => _tags;

    public string Zone => GetString(GameTag.Zone);

    public int Controller => GetInt(GameTag.Controller);

    public string CardType => GetString(GameTag.CardType);

    public int ZonePosition => GetInt(GameTag.ZonePosition);

    public bool IsMinion => CardType == "MINION";

    public bool IsHero => CardType == "HERO";

    public bool InPlay => Zone == "PLAY";

    public bool InHand => Zone == "HAND";

    public bool InDeck => Zone == "DECK";

    public int Attack => GetInt(GameTag.Atk);

    public int Health => GetInt(GameTag.Health) - GetInt(GameTag.Damage);

    // Tracking metadata, maintained by GameState.
    public int CreatedTurn { get; internal set; }

    public string InitialZone { get; internal set; } = string.Empty;

    public int InitialController { get; internal set; }

    public int EnteredHandTurn { get; internal set; } = -1;

    public int LeftHandTurn { get; internal set; } = -1;

    public bool Mulliganed { get; internal set; }

    public bool WasRevealed { get; internal set; }

    /// <summary>True when the card was part of the starting deck (not generated mid-game).</summary>
    public bool IsOriginalDeckCard =>
        CreatedTurn == 0
        && (InitialZone is "DECK" or "HAND")
        && GetInt(GameTag.Creator) == 0
        && CardId != "GAME_005";

    public int GetInt(string tag)
    {
        if (!_tags.TryGetValue(tag, out var value))
        {
            return 0;
        }

        return int.TryParse(value, out var result) ? result : GameTag.EnumValue(tag, value);
    }

    public string GetString(string tag) => _tags.TryGetValue(tag, out var value) ? value : string.Empty;

    public bool Has(string tag) => GetInt(tag) != 0;

    internal string SetTag(string tag, string value)
    {
        var previous = GetString(tag);
        _tags[tag] = value;
        return previous;
    }

    public override string ToString() => $"#{Id} {CardId} {Name} [{Zone}] c={Controller}";
}
