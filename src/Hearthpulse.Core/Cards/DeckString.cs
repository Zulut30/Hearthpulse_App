using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Cards;

public sealed record DeckDefinition(string Name, long DeckId, string Code, string Format, IReadOnlyList<int> HeroDbfIds, IReadOnlyDictionary<int, int> Cards)
{
    public int CardCount => Cards.Values.Sum();
}

/// <summary>Decoder for Blizzard deck codes (base64 varint stream).</summary>
public static class DeckString
{
    public static DeckDefinition? TryDecode(string code, string name = "", long deckId = 0)
    {
        try
        {
            var bytes = Convert.FromBase64String(code.Trim());
            var position = 0;
            if (ReadVarint(bytes, ref position) != 0 || ReadVarint(bytes, ref position) != 1)
            {
                return null;
            }

            var format = ReadVarint(bytes, ref position) switch
            {
                1 => "Wild",
                2 => "Standard",
                3 => "Classic",
                4 => "Twist",
                _ => "Unknown",
            };

            var heroes = new List<int>();
            for (var count = ReadVarint(bytes, ref position); count > 0; count--)
            {
                heroes.Add(ReadVarint(bytes, ref position));
            }

            var cards = new Dictionary<int, int>();
            for (var copies = 1; copies <= 3; copies++)
            {
                var count = ReadVarint(bytes, ref position);
                for (var i = 0; i < count; i++)
                {
                    var dbf = ReadVarint(bytes, ref position);
                    var n = copies == 3 ? ReadVarint(bytes, ref position) : copies;
                    cards[dbf] = cards.GetValueOrDefault(dbf) + n;
                }
            }

            return new DeckDefinition(name, deckId, code.Trim(), format, heroes, cards);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The default hero card of a class (<c>PRIEST</c> → <c>HERO_09</c>), the hero a deck code names.</summary>
    public static string? ClassHero(string? playerClass) => playerClass?.ToUpperInvariant() switch
    {
        "WARRIOR" => "HERO_01",
        "SHAMAN" => "HERO_02",
        "ROGUE" => "HERO_03",
        "PALADIN" => "HERO_04",
        "HUNTER" => "HERO_05",
        "DRUID" => "HERO_06",
        "WARLOCK" => "HERO_07",
        "MAGE" => "HERO_08",
        "PRIEST" => "HERO_09",
        "DEMONHUNTER" => "HERO_10",
        "DEATHKNIGHT" => "HERO_11",
        _ => null,
    };

    /// <summary>
    /// The deck code of a list of cards (card id → copies) for a class, as the game writes it: version 1, the format,
    /// the class's default hero, the cards by copies sorted by dbf id, no sideboards. Null when a card or the hero is
    /// not in <paramref name="cards"/>, or the list is empty.
    /// </summary>
    public static string? Encode(IReadOnlyDictionary<string, int> deck, string playerClass, string format, CardDb cards)
    {
        if (deck.Count == 0 || cards.Get(ClassHero(playerClass)) is not { DbfId: > 0 } hero)
        {
            return null;
        }

        var dbf = new List<(int Dbf, int Copies)>(deck.Count);
        foreach (var (id, copies) in deck)
        {
            if (copies <= 0 || cards.Get(id) is not { DbfId: > 0 } card)
            {
                return null;
            }

            dbf.Add((card.DbfId, copies));
        }

        var bytes = new List<byte>(64);
        void Write(int value)
        {
            var v = (uint)value;
            while (v >= 0x80)
            {
                bytes.Add((byte)(v | 0x80));
                v >>= 7;
            }

            bytes.Add((byte)v);
        }

        Write(0);
        Write(1);
        Write(format switch { "Wild" => 1, "Standard" => 2, "Classic" => 3, "Twist" => 4, _ => 1 });
        Write(1);
        Write(hero.DbfId);
        foreach (var group in new[] { 1, 2, 3 })
        {
            var cardsOf = dbf.Where(c => group == 3 ? c.Copies >= 3 : c.Copies == group).OrderBy(c => c.Dbf).ToList();
            Write(cardsOf.Count);
            foreach (var (id, copies) in cardsOf)
            {
                Write(id);
                if (group == 3)
                {
                    Write(copies);
                }
            }
        }

        Write(0);
        return Convert.ToBase64String(bytes.ToArray());
    }

    private static int ReadVarint(byte[] data, ref int position)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[position++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }
}

/// <summary>Battlegrounds tribes: TAG_RACE ids and display names.</summary>
public static class Races
{
    public static readonly IReadOnlyDictionary<int, string> ById = new Dictionary<int, string>
    {
        [11] = "UNDEAD", [14] = "MURLOC", [15] = "DEMON", [17] = "MECHANICAL", [18] = "ELEMENTAL",
        [20] = "BEAST", [23] = "PIRATE", [24] = "DRAGON", [26] = "ALL", [43] = "QUILBOAR",
        [92] = "NAGA", [126] = "ABERRATION",
    };

    private static readonly Dictionary<string, (string En, string It)> Names = new()
    {
        ["BEAST"] = ("Beast", "Bestia"),
        ["DEMON"] = ("Demon", "Demone"),
        ["DRAGON"] = ("Dragon", "Drago"),
        ["ELEMENTAL"] = ("Elemental", "Elementale"),
        ["MECHANICAL"] = ("Mech", "Robot"),
        ["MURLOC"] = ("Murloc", "Murloc"),
        ["NAGA"] = ("Naga", "Naga"),
        ["PIRATE"] = ("Pirate", "Pirata"),
        ["QUILBOAR"] = ("Quilboar", "Verrospino"),
        ["UNDEAD"] = ("Undead", "Non Morto"),
        ["ABERRATION"] = ("Aberration", "Aberrazione"),
        ["ALL"] = ("All", "Tutti"),
    };

    /// <summary>
    /// The tribe's name: Italian for Italian card data, else in the interface language (the card data has no tribe
    /// names, so they live in the string tables).
    /// </summary>
    public static string Display(string race, string locale)
    {
        if (!Names.TryGetValue(race, out var names))
        {
            return race.Length > 1 ? race[0] + race[1..].ToLowerInvariant() : race;
        }

        return locale.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? names.It : Loc.Or("Core.Tribe." + race, names.En);
    }
}
