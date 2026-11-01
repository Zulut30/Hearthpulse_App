using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Hearthpulse.Core.Migration;

/// <summary>A game as Hearthstone Deck Tracker saved it (<c>GameStats</c> in DeckStats.xml / DefaultDeckStats.xml).</summary>
internal sealed record HdtGame
{
    public Guid GameId { get; init; }

    public Guid DeckId { get; init; }

    public string DeckName { get; init; } = string.Empty;

    public Version? DeckVersion { get; init; }

    public DateTimeOffset Start { get; init; }

    public DateTimeOffset End { get; init; }

    /// <summary><c>Win</c>, <c>Loss</c>, <c>Draw</c> or <c>None</c> (not finished).</summary>
    public string Result { get; init; } = "None";

    /// <summary>True when the player went second.</summary>
    public bool Coin { get; init; }

    /// <summary>The game turn number at the end.</summary>
    public int Turns { get; init; }

    public string GameMode { get; init; } = string.Empty;

    public string GameType { get; init; } = string.Empty;

    /// <summary><c>Standard</c>, <c>Wild</c>, <c>Classic</c>, <c>Twist</c>; only for Ranked and Casual.</summary>
    public string? Format { get; init; }

    public string PlayerName { get; init; } = string.Empty;

    public string OpponentName { get; init; } = string.Empty;

    /// <summary>A class name (<c>Priest</c>, <c>DemonHunter</c>) or a hero's English name.</summary>
    public string PlayerHero { get; init; } = string.Empty;

    public string OpponentHero { get; init; } = string.Empty;

    public string? PlayerHeroCardId { get; init; }

    public string? OpponentHeroCardId { get; init; }

    public int LeagueId { get; init; }

    public int StarLevel { get; init; }

    public int LegendRank { get; init; }

    public int? BattlegroundsRating { get; init; }

    public int? BattlegroundsRatingAfter { get; init; }

    public int? Placement { get; init; }

    public int? AnomalyDbfId { get; init; }

    /// <summary>Opponent cards the game revealed (card id, copies).</summary>
    public IReadOnlyList<(string CardId, int Count)> OpponentCards { get; init; } = [];

    /// <summary>The file in <c>Replays\</c>, when HDT kept one.</summary>
    public string? ReplayFile { get; init; }

    /// <summary>A copy made when a deck was cloned "with stats": never imported.</summary>
    public bool IsClone { get; init; }
}

/// <summary>A deck of PlayerDecks.xml with its older versions.</summary>
internal sealed record HdtDeck
{
    public Guid DeckId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>A class name as HDT writes it (<c>DemonHunter</c>).</summary>
    public string Class { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> Cards { get; init; } = new Dictionary<string, int>();

    public Version Version { get; init; } = new(1, 0);

    /// <summary>Older versions and their lists.</summary>
    public IReadOnlyList<(Version Version, IReadOnlyDictionary<string, int> Cards)> History { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string Note { get; init; } = string.Empty;

    public bool Archived { get; init; }

    public DateTimeOffset? LastEdited { get; init; }

    /// <summary>An Arena run, a Dungeon Run or a Duels deck: not a deck of the library.</summary>
    public bool Special { get; init; }

    /// <summary>The list a game played with this version (the current one when the version is unknown).</summary>
    public IReadOnlyDictionary<string, int> CardsOf(Version? version) =>
        version is null || version == Version ? Cards : History.FirstOrDefault(h => h.Version == version).Cards ?? Cards;
}

/// <summary>A Battlegrounds game of BgsLastGames.xml: hero, rating, place and the final board.</summary>
internal sealed record HdtBgGame(DateTimeOffset Start, string Hero, int? Rating, int? RatingAfter, int Placement, IReadOnlyList<(string CardId, int Attack, int Health, bool Golden)> Board);

/// <summary>
/// Reads Hearthstone Deck Tracker's data files (<c>docs/migration.md</c>): XmlSerializer output, UTF-8, an absent element
/// means its default. Read-only, with sharing (HDT may be running), falling back to the <c>.bak</c> copy HDT keeps; no DTD,
/// no external entities, bounded size. Only the fields the import uses are read: never <c>ServerInfo</c>.
/// </summary>
internal static class HdtData
{
    /// <summary>A file larger than this is refused (real ones are a few MB).</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The root of a file, or null when it is missing; throws <see cref="InvalidDataException"/> when it is too large or broken (and so is its .bak).</summary>
    public static XElement? Load(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            return null;
        }

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (new FileInfo(candidate).Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"{Path.GetFileName(candidate)} is over {MaxFileBytes / 1024 / 1024} MB");
                }

                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxFileBytes,
                    IgnoreComments = true,
                });
                return XDocument.Load(reader).Root;
            }
            catch (XmlException ex)
            {
                Diag.Info($"HDT file {Path.GetFileName(candidate)} could not be read: {ex.Message}");
            }
        }

        throw new InvalidDataException($"{Path.GetFileName(path)} is damaged");
    }

    /// <summary>The games of DeckStats.xml and DefaultDeckStats.xml (a game's deck id from its deck entry when missing).</summary>
    public static IEnumerable<HdtGame> Games(XElement? root) =>
        root?.Elements("DeckStats").Elements()
            .SelectMany(deck => deck.Element("Games")?.Elements("Game").Select(g => Game(g, Guid(deck.Element("DeckId")))) ?? [])
        ?? [];

    public static IEnumerable<HdtDeck> Decks(XElement? root) => root?.Elements("Deck").Select(Deck) ?? [];

    public static IEnumerable<HdtBgGame> BattlegroundsGames(XElement? root) =>
        root?.Elements("Game").Select(g => new HdtBgGame(
            Time((string?)g.Attribute("StartTime")),
            (string?)g.Attribute("Hero") ?? string.Empty,
            Int((string?)g.Attribute("Rating")),
            Int((string?)g.Attribute("RatingAfter")) is > 0 and var after ? after : null,
            // The attribute's name is misspelt in HDT's files.
            Int((string?)g.Attribute("Placemenent")) ?? Int((string?)g.Attribute("Placement")) ?? 0,
            g.Element("FinalBoard")?.Elements("Minion").Select(Minion).ToList() ?? []))
        ?? [];

    private static HdtGame Game(XElement g, Guid deckId)
    {
        string Text(string name) => g.Element(name)?.Value ?? string.Empty;
        string? Optional(string name) => g.Element(name)?.Value is { Length: > 0 } value ? value : null;
        int Number(string name) => Int(Optional(name)) ?? 0;

        var details = g.Element("BattlegroundsDetails");
        return new HdtGame
        {
            GameId = Guid(g.Element("GameId")),
            DeckId = Guid(g.Element("DeckId")) is var own && own != System.Guid.Empty ? own : deckId,
            DeckName = Text("DeckName"),
            DeckVersion = VersionOf(g.Element("PlayerDeckVersion")),
            Start = Time(Optional("StartTime")),
            End = Time(Optional("EndTime")),
            Result = Optional("Result") ?? "None",
            Coin = Optional("Coin") == "true",
            Turns = Number("Turns"),
            GameMode = Text("GameMode"),
            GameType = Text("GameType"),
            Format = Optional("Format"),
            PlayerName = Text("PlayerName"),
            OpponentName = Text("OpponentName"),
            PlayerHero = Text("PlayerHero"),
            OpponentHero = Text("OpponentHero"),
            PlayerHeroCardId = Optional("PlayerHeroCardId"),
            OpponentHeroCardId = Optional("OpponentHeroCardId"),
            LeagueId = Number("LeagueId"),
            StarLevel = Number("StarLevel"),
            LegendRank = Number("LegendRank"),
            BattlegroundsRating = Int(Optional("BattlegroundsRating")),
            BattlegroundsRatingAfter = Int(Optional("BattlegroundsRatingAfter")),
            Placement = Int(details?.Element("FinalPlacement")?.Value),
            AnomalyDbfId = Int(details?.Element("AnomalyDbfId")?.Value),
            OpponentCards = g.Element("OpponentCards")?.Elements("Card")
                .Select(c => ((string?)c.Attribute("Id") ?? string.Empty, Int((string?)c.Attribute("Count")) ?? 1))
                .Where(c => c.Item1.Length > 0).ToList() ?? [],
            ReplayFile = Optional("ReplayFile"),
            IsClone = Optional("IsClone") == "true",
        };
    }

    private static HdtDeck Deck(XElement d)
    {
        var history = d.Element("DeckHistory")?.Elements("Deck")
            .Select(old => (VersionOf(old.Element("Version")) ?? new Version(1, 0), (IReadOnlyDictionary<string, int>)Cards(old)))
            .ToList() ?? [];
        return new HdtDeck
        {
            DeckId = Guid(d.Element("DeckId")),
            Name = d.Element("Name")?.Value ?? string.Empty,
            Class = d.Element("Class")?.Value ?? string.Empty,
            Cards = Cards(d),
            Version = VersionOf(d.Element("Version")) ?? new Version(1, 0),
            History = history,
            Tags = d.Element("Tags")?.Elements("Tag").Select(t => t.Value).Where(t => t is { Length: > 0 } and not ("All" or "Favorite" or "None")).ToList() ?? [],
            Note = d.Element("Note")?.Value ?? string.Empty,
            Archived = d.Element("Archived")?.Value == "true",
            LastEdited = d.Element("LastEdited")?.Value is { Length: > 0 } edited ? Time(edited) : null,
            Special = d.Element("IsArenaDeck")?.Value == "true" || d.Element("IsDungeonDeck")?.Value == "true" || d.Element("IsDuelsDeck")?.Value == "true",
        };
    }

    private static Dictionary<string, int> Cards(XElement deck)
    {
        var cards = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in deck.Element("Cards")?.Elements("Card") ?? [])
        {
            if (card.Element("Id")?.Value is { Length: > 0 } id)
            {
                cards[id] = cards.GetValueOrDefault(id) + (Int(card.Element("Count")?.Value) ?? 1);
            }
        }

        return cards;
    }

    private static (string, int, int, bool) Minion(XElement m)
    {
        // Tags are the game's numeric tags: ATK 47, HEALTH 45, DAMAGE 44, PREMIUM 12.
        var tags = m.Elements("Tags").ToDictionary(t => Int((string?)t.Attribute("Tag")) ?? -1, t => Int((string?)t.Attribute("Value")) ?? 0);
        return (m.Element("CardId")?.Value ?? string.Empty, tags.GetValueOrDefault(47), tags.GetValueOrDefault(45) - tags.GetValueOrDefault(44), tags.GetValueOrDefault(12) > 0);
    }

    private static Version? VersionOf(XElement? v) =>
        v is null ? null : new Version(Int(v.Element("Major")?.Value) ?? 1, Int(v.Element("Minor")?.Value) ?? 0);

    private static Guid Guid(XElement? element) =>
        System.Guid.TryParse(element?.Value, out var id) ? id : System.Guid.Empty;

    private static int? Int(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset Time(string? text)
    {
        try
        {
            return text is { Length: > 0 } ? XmlConvert.ToDateTimeOffset(text) : default;
        }
        catch (FormatException)
        {
            return default;
        }
    }
}
