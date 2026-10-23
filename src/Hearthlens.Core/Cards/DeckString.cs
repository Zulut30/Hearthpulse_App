namespace Hearthlens.Core.Cards;

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

    public static string Display(string race, string locale)
    {
        if (!Names.TryGetValue(race, out var names))
        {
            return race.Length > 1 ? race[0] + race[1..].ToLowerInvariant() : race;
        }

        return locale.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? names.It : names.En;
    }
}
