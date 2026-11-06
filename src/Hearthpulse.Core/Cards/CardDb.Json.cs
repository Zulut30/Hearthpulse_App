using System.Text.Json;

namespace Hearthpulse.Core.Cards;

public sealed partial class CardDb
{
    /// <summary>
    /// Reads the HearthstoneJSON card array in 64 KB chunks. A <see cref="JsonDocument"/> of the 12 MB file kept ~50 MB
    /// of buffers rented from the shared array pool alive after parsing, and a buffer of the whole file lands on the
    /// large object heap; the chunks stay below it. Values of an unexpected type read as empty, as before; a body that is
    /// not a JSON array throws <see cref="JsonException"/>.
    /// </summary>
    internal static List<Card> ParseList(Stream json)
    {
        var list = new CardListReader(json.CanSeek ? (int)Math.Min(json.Length / 350, 100_000) : 0);
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (true)
        {
            var read = json.Read(buffer, length, buffer.Length - length);
            length += read;
            var final = read == 0;
            var consumed = list.Consume(buffer.AsSpan(0, length), final);
            if (final)
            {
                return list.Cards;
            }

            buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
            length -= consumed;
            if (length == buffer.Length)
            {
                // One card larger than the buffer.
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
    }

    /// <inheritdoc cref="ParseList(Stream)"/>
    internal static List<Card> ParseList(ReadOnlySpan<byte> json)
    {
        var list = new CardListReader(json.Length / 350);
        list.Consume(json, isFinalBlock: true);
        return list.Cards;
    }

    /// <summary>The card array fed in chunks: each card is read once it is complete in the chunk.</summary>
    private sealed class CardListReader(int capacity)
    {
        private readonly StringPool _strings = new();
        private JsonReaderState _state;
        private bool _bomChecked;
        private bool _started;
        private bool _ended;

        // About 350 bytes per card in HearthstoneJSON.
        public List<Card> Cards { get; } = new(capacity);

        /// <summary>Reads what is complete in <paramref name="data"/>; returns how many bytes it used.</summary>
        public int Consume(ReadOnlySpan<byte> data, bool isFinalBlock)
        {
            var offset = 0;
            if (!_bomChecked)
            {
                if (data.Length < 3 && !isFinalBlock)
                {
                    return 0;
                }

                // A byte order mark is valid in a file, but not for Utf8JsonReader.
                _bomChecked = true;
                offset = data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            }

            var reader = new Utf8JsonReader(data[offset..], isFinalBlock, _state);
            var checkpoint = reader;
            while (reader.Read())
            {
                if (!_started)
                {
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        throw new JsonException("The card data is not a JSON array.");
                    }

                    _started = true;
                }
                else if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 0)
                {
                    // Anything after the array other than white space makes a later Read throw.
                    _ended = true;
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var probe = reader;
                    if (!probe.TrySkip())
                    {
                        break;
                    }

                    if (ReadCard(ref reader, _strings) is { } card)
                    {
                        Cards.Add(card);
                    }
                }
                else if (!reader.TrySkip())
                {
                    break;
                }

                checkpoint = reader;
            }

            if (isFinalBlock && !_ended)
            {
                throw new JsonException("The card data ends before the card array does.");
            }

            _state = checkpoint.CurrentState;
            return offset + (int)checkpoint.BytesConsumed;
        }
    }

    private static Card? ReadCard(ref Utf8JsonReader reader, StringPool strings)
    {
        string id = string.Empty, name = string.Empty, text = string.Empty, type = string.Empty, rarity = string.Empty,
            cardClass = string.Empty, set = string.Empty, race = string.Empty;
        int dbfId = 0, cost = 0, attack = 0, health = 0, armor = 0, techLevel = 0, premiumDbfId = 0, normalDbfId = 0, skinParentId = 0;
        bool collectible = false, poolMinion = false, poolSpell = false, hero = false, duosExclusive = false;
        IReadOnlyList<string> races = Array.Empty<string>(), mechanics = Array.Empty<string>(), runes = Array.Empty<string>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var field = FieldOf(ref reader);
            reader.Read();
            switch (field)
            {
                case CardField.Id: id = ReadString(ref reader); break;
                case CardField.DbfId: dbfId = ReadInt(ref reader); break;
                case CardField.Name: name = ReadString(ref reader); break;
                case CardField.Text: text = ReadString(ref reader); break;
                case CardField.Cost: cost = ReadInt(ref reader); break;
                case CardField.Attack: attack = ReadInt(ref reader); break;
                case CardField.Health: health = ReadInt(ref reader); break;
                case CardField.Armor: armor = ReadInt(ref reader); break;
                case CardField.Type: type = strings.Get(ReadString(ref reader)); break;
                case CardField.Rarity: rarity = strings.Get(ReadString(ref reader)); break;
                case CardField.CardClass: cardClass = strings.Get(ReadString(ref reader)); break;
                case CardField.Set: set = strings.Get(ReadString(ref reader)); break;
                case CardField.Race: race = strings.Get(ReadString(ref reader)); break;
                case CardField.Races: races = ReadStrings(ref reader, strings); break;
                case CardField.Mechanics: mechanics = ReadStrings(ref reader, strings); break;
                case CardField.RuneCost: runes = ReadRunes(ref reader, strings); break;
                case CardField.Collectible: collectible = ReadBool(ref reader); break;
                case CardField.TechLevel: techLevel = ReadInt(ref reader); break;
                case CardField.PoolMinion: poolMinion = ReadBool(ref reader); break;
                case CardField.PoolSpell: poolSpell = ReadBool(ref reader); break;
                case CardField.Hero: hero = ReadBool(ref reader); break;
                case CardField.DuosExclusive: duosExclusive = ReadBool(ref reader); break;
                case CardField.PremiumDbfId: premiumDbfId = ReadInt(ref reader); break;
                case CardField.NormalDbfId: normalDbfId = ReadInt(ref reader); break;
                case CardField.SkinParentId: skinParentId = ReadInt(ref reader); break;
                default: SkipValue(ref reader); break;
            }
        }

        if (id.Length == 0)
        {
            return null;
        }

        return new Card
        {
            Id = id,
            DbfId = dbfId,
            Name = name,
            Text = text,
            Cost = cost,
            Attack = attack,
            Health = health,
            Armor = armor,
            Type = type,
            Rarity = rarity,
            CardClass = cardClass,
            Set = set,
            Races = races.Count > 0 ? races : race.Length > 0 ? [race] : Array.Empty<string>(),
            Mechanics = mechanics,
            Runes = runes,
            Collectible = collectible,
            TechLevel = techLevel,
            IsBattlegroundsPoolMinion = poolMinion,
            IsBattlegroundsPoolSpell = poolSpell,
            IsBattlegroundsHero = hero,
            IsBattlegroundsDuosExclusive = duosExclusive,
            BattlegroundsPremiumDbfId = premiumDbfId,
            BattlegroundsNormalDbfId = normalDbfId,
            BattlegroundsSkinParentId = skinParentId,
        };
    }

    private enum CardField
    {
        Other, Id, DbfId, Name, Text, Cost, Attack, Health, Armor, Type, Rarity, CardClass, Set, Race, Races, Mechanics,
        RuneCost, Collectible, TechLevel, PoolMinion, PoolSpell, Hero, DuosExclusive, PremiumDbfId, NormalDbfId, SkinParentId,
    }

    private static CardField FieldOf(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("id"u8) ? CardField.Id
        : reader.ValueTextEquals("dbfId"u8) ? CardField.DbfId
        : reader.ValueTextEquals("name"u8) ? CardField.Name
        : reader.ValueTextEquals("text"u8) ? CardField.Text
        : reader.ValueTextEquals("cost"u8) ? CardField.Cost
        : reader.ValueTextEquals("attack"u8) ? CardField.Attack
        : reader.ValueTextEquals("health"u8) ? CardField.Health
        : reader.ValueTextEquals("armor"u8) ? CardField.Armor
        : reader.ValueTextEquals("type"u8) ? CardField.Type
        : reader.ValueTextEquals("rarity"u8) ? CardField.Rarity
        : reader.ValueTextEquals("cardClass"u8) ? CardField.CardClass
        : reader.ValueTextEquals("set"u8) ? CardField.Set
        : reader.ValueTextEquals("race"u8) ? CardField.Race
        : reader.ValueTextEquals("races"u8) ? CardField.Races
        : reader.ValueTextEquals("mechanics"u8) ? CardField.Mechanics
        : reader.ValueTextEquals("runeCost"u8) ? CardField.RuneCost
        : reader.ValueTextEquals("collectible"u8) ? CardField.Collectible
        : reader.ValueTextEquals("techLevel"u8) ? CardField.TechLevel
        : reader.ValueTextEquals("isBattlegroundsPoolMinion"u8) ? CardField.PoolMinion
        : reader.ValueTextEquals("isBattlegroundsPoolSpell"u8) ? CardField.PoolSpell
        : reader.ValueTextEquals("battlegroundsHero"u8) ? CardField.Hero
        : reader.ValueTextEquals("isBattlegroundsDuosExclusive"u8) ? CardField.DuosExclusive
        : reader.ValueTextEquals("battlegroundsPremiumDbfId"u8) ? CardField.PremiumDbfId
        : reader.ValueTextEquals("battlegroundsNormalDbfId"u8) ? CardField.NormalDbfId
        : reader.ValueTextEquals("battlegroundsSkinParentId"u8) ? CardField.SkinParentId
        : CardField.Other;

    /// <summary>A string value, else empty (an object or array value is skipped).</summary>
    private static string ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }

        SkipValue(ref reader);
        return string.Empty;
    }

    /// <summary>True only for a true value; any other value (an object or array is skipped) reads as false.</summary>
    private static bool ReadBool(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.TokenType == JsonTokenType.True;
        }

        SkipValue(ref reader);
        return false;
    }

    private static int ReadInt(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.TryGetInt32(out var value) ? value : 0;
        }

        SkipValue(ref reader);
        return 0;
    }

    /// <summary>The strings of an array value (other items are ignored), else empty.</summary>
    private static IReadOnlyList<string> ReadStrings(ref Utf8JsonReader reader, StringPool strings)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            SkipValue(ref reader);
            return Array.Empty<string>();
        }

        List<string>? list = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                (list ??= new List<string>(2)).Add(strings.Get(reader.GetString() ?? string.Empty));
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        return list is null ? Array.Empty<string>() : list.ToArray();
    }

    /// <summary>The rune types of <c>runeCost</c> with a count above 0, upper case; see <see cref="RunesOf"/>.</summary>
    private static IReadOnlyList<string> ReadRunes(ref Utf8JsonReader reader, StringPool strings)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            SkipValue(ref reader);
            return Array.Empty<string>();
        }

        List<string>? runes = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var rune = reader.GetString() ?? string.Empty;
            reader.Read();
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var count) && count > 0)
            {
                (runes ??= new List<string>(3)).Add(strings.Get(rune.ToUpperInvariant()));
            }
            else
            {
                SkipValue(ref reader);
            }
        }

        return runes is null ? Array.Empty<string>() : runes.ToArray();
    }

    /// <summary>Skips an object or array value. Cards are read only once complete in the chunk, so it never runs out of data.</summary>
    private static void SkipValue(ref Utf8JsonReader reader) => reader.TrySkip();

    /// <summary>
    /// One instance per distinct value: type, rarity, class, set, races, mechanics and runes repeat across tens of
    /// thousands of cards (5 MB of duplicate strings for ruRU).
    /// </summary>
    private sealed class StringPool
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        public string Get(string value)
        {
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (_strings.TryGetValue(value, out var known))
            {
                return known;
            }

            _strings[value] = value;
            return value;
        }
    }
}
