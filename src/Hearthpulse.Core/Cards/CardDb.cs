using System.Text.Json;

namespace Hearthpulse.Core.Cards;

/// <summary>
/// Card metadata from HearthstoneJSON, supplemented by hsbg.cards (tavern tiers, text and current-patch pool
/// cards) and cached on disk per locale.
/// </summary>
public sealed class CardDb
{
    public const string Attribution = "Card data: HearthstoneJSON · hsbg.cards";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private readonly Dictionary<string, Card> _byId;
    private readonly Dictionary<int, Card> _byDbf;

    private CardDb(string locale, IReadOnlyList<Card> cards, BgCardDb? supplement = null)
    {
        Locale = locale;
        BgCards = supplement ?? BgCardDb.Empty;
        // Sized up front: growing to 36 000 cards re-allocated the tables on the large object heap at every doubling,
        // and each of those allocations counts toward a gen2 collection (three of them during start-up).
        _byId = new Dictionary<string, Card>(cards.Count, StringComparer.OrdinalIgnoreCase);
        _byDbf = new Dictionary<int, Card>(cards.Count);
        foreach (var card in cards)
        {
            _byId[card.Id] = card;
            if (card.DbfId > 0)
            {
                _byDbf[card.DbfId] = card;
            }
        }

        BattlegroundsMinions = _byId.Values
            .Where(c => c.IsBattlegroundsPoolMinion && c.TechLevel > 0)
            .OrderBy(c => c.TechLevel).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
        BattlegroundsSpells = _byId.Values
            .Where(c => c.IsBattlegroundsPoolSpell && c.TechLevel > 0)
            .OrderBy(c => c.TechLevel).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    public static CardDb Empty { get; } = new("enUS", Array.Empty<Card>());

    public string Locale { get; }

    public int Count => _byId.Count;

    /// <summary>Every card, in no particular order.</summary>
    public IEnumerable<Card> All => _byId.Values;

    public IReadOnlyList<Card> BattlegroundsMinions { get; }

    public IReadOnlyList<Card> BattlegroundsSpells { get; }

    /// <summary>The hsbg.cards supplement behind <see cref="Attribution"/>, empty when unavailable.</summary>
    public BgCardDb BgCards { get; }

    public Card? Get(string? cardId) => cardId is { Length: > 0 } && _byId.TryGetValue(cardId, out var card) ? card : null;

    public Card? Get(int dbfId) => _byDbf.GetValueOrDefault(dbfId);

    public string NameOf(string? cardId) => Get(cardId)?.Name ?? cardId ?? string.Empty;

    /// <summary>
    /// Card name from the database, else <paramref name="fallback"/> (e.g. the name the client printed in the log),
    /// else the card id.
    /// </summary>
    public string NameOr(string? cardId, string? fallback) =>
        Get(cardId)?.Name is { Length: > 0 } name ? name
        : !string.IsNullOrEmpty(fallback) ? fallback
        : cardId ?? string.Empty;

    /// <summary>
    /// True when the card is in the current patch's Battlegrounds tavern pool (a pool minion or a tavern spell with
    /// a tier). Unknown cards count as available, so nothing is hidden while the card feeds have not loaded.
    /// </summary>
    public bool InBattlegroundsPool(string? cardId)
    {
        if (BattlegroundsMinions.Count == 0 && BattlegroundsSpells.Count == 0)
        {
            return true;
        }

        return Get(cardId) is { TechLevel: > 0 } card
            && (card.IsBattlegroundsPoolMinion || card.IsBattlegroundsPoolSpell);
    }

    /// <summary>Loads from cache, refreshing from the network when stale. Never throws.</summary>
    public static Task<CardDb> LoadAsync(string locale, HttpClient http, CancellationToken cancellationToken = default) =>
        // With a fresh cache nothing below awaits anything, so the whole parse (half a second, tens of MB) ran on the
        // caller's thread - the UI thread at start-up - and froze the window that had just appeared.
        Task.Run(() => LoadCoreAsync(locale, http, cancellationToken), CancellationToken.None);

    private static async Task<CardDb> LoadCoreAsync(string locale, HttpClient http, CancellationToken cancellationToken)
    {
        var cards = await LoadCardsAsync(locale, http, cancellationToken).ConfigureAwait(false);
        var supplement = await BgCardDb.LoadAsync(http, cancellationToken).ConfigureAwait(false);
        return new CardDb(locale, ApplySupplement(cards, supplement), supplement);
    }

    private static async Task<List<Card>> LoadCardsAsync(string locale, HttpClient http, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppPaths.Cache, $"cards.{locale}.json");
        var file = new FileInfo(path);
        if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > MaxAge)
        {
            try
            {
                var url = $"https://api.hearthstonejson.com/v1/latest/{locale}/cards.json";
                var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                // Validate before replacing a working cache with a truncated or error body.
                ParseList(bytes);

                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                Diag.Info($"Card data refreshed ({locale}, {bytes.Length:N0} bytes)");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or JsonException)
            {
                Diag.Error("Card data download failed", ex);
            }
        }

        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return ParseList(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Card data unreadable", ex);
            return [];
        }
    }

    public static CardDb Parse(string locale, Stream json) => new(locale, ParseList(json));

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

    /// <summary>
    /// Overlays the hsbg.cards supplement on the HearthstoneJSON cards: fills fields HearthstoneJSON is missing
    /// (tavern tier, text, races, keywords, golden ids) and appends current-patch pool cards it does not know yet.
    /// </summary>
    internal static List<Card> ApplySupplement(List<Card> cards, BgCardDb supplement)
    {
        if (supplement.Count == 0)
        {
            return cards;
        }

        var byId = new Dictionary<string, int>(cards.Count + supplement.Count, StringComparer.OrdinalIgnoreCase);
        var byDbf = new Dictionary<int, int>(cards.Count + supplement.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            byId[cards[i].Id] = i;
            if (cards[i].DbfId > 0)
            {
                byDbf[cards[i].DbfId] = i;
            }
        }

        foreach (var b in supplement.Cards)
        {
            if (byId.TryGetValue(b.ExternalId, out var index) || (b.Id > 0 && byDbf.TryGetValue(b.Id, out index)))
            {
                var patched = Patch(cards[index], b);
                if (!ReferenceEquals(patched, cards[index]))
                {
                    cards[index] = patched;
                    byId[patched.Id] = index;
                }

                continue;
            }

            if (!b.IsPoolCard || (!b.IsMinion && !b.IsSpell))
            {
                continue;
            }

            var added = NewPoolCard(b);
            byId[added.Id] = cards.Count;
            if (added.DbfId > 0)
            {
                byDbf[added.DbfId] = cards.Count;
            }

            cards.Add(added);
        }

        return cards;
    }

    private static Card Patch(Card card, BgCard b)
    {
        var techLevel = card.TechLevel > 0 ? card.TechLevel : b.TechLevel;
        var text = card.Text.Length > 0 ? card.Text : b.Text;
        var name = card.Name.Length > 0 ? card.Name : b.Name;
        var races = card.Races.Count > 0 ? card.Races : MapRaces(b.MinionTypes);
        var mechanics = card.Mechanics.Count > 0 ? card.Mechanics : MapMechanics(b.Keywords);
        var premium = card.BattlegroundsPremiumDbfId > 0 ? card.BattlegroundsPremiumDbfId : b.GoldenDbfId;
        var normal = card.BattlegroundsNormalDbfId;
        if (normal == 0 && b.GoldenDbfId > 0 && card.DbfId == b.GoldenDbfId && b.Id > 0)
        {
            normal = b.Id;
        }

        var poolMinion = card.IsBattlegroundsPoolMinion || (b.IsPoolCard && b.IsMinion);
        var poolSpell = card.IsBattlegroundsPoolSpell || (b.IsPoolCard && b.IsSpell);
        var hero = card.IsBattlegroundsHero || b.IsHero;
        var duos = card.IsBattlegroundsDuosExclusive || b.IsDuosOnly;
        var skinParent = card.BattlegroundsSkinParentId;
        if (skinParent == 0 && b.IsHeroSkin && b.ParentId > 0)
        {
            skinParent = b.ParentId;
        }

        if (techLevel == card.TechLevel && text == card.Text && name == card.Name && ReferenceEquals(races, card.Races)
            && ReferenceEquals(mechanics, card.Mechanics) && premium == card.BattlegroundsPremiumDbfId
            && normal == card.BattlegroundsNormalDbfId && poolMinion == card.IsBattlegroundsPoolMinion
            && poolSpell == card.IsBattlegroundsPoolSpell && hero == card.IsBattlegroundsHero
            && duos == card.IsBattlegroundsDuosExclusive && skinParent == card.BattlegroundsSkinParentId)
        {
            return card;
        }

        return new Card
        {
            Id = card.Id,
            DbfId = card.DbfId,
            Name = name,
            Text = text,
            Cost = card.Cost,
            Attack = card.Attack,
            Health = card.Health,
            Armor = card.Armor,
            Type = card.Type,
            Rarity = card.Rarity,
            CardClass = card.CardClass,
            Set = card.Set,
            Races = races,
            Mechanics = mechanics,
            Runes = card.Runes,
            Collectible = card.Collectible,
            TechLevel = techLevel,
            IsBattlegroundsPoolMinion = poolMinion,
            IsBattlegroundsPoolSpell = poolSpell,
            IsBattlegroundsHero = hero,
            IsBattlegroundsDuosExclusive = duos,
            BattlegroundsPremiumDbfId = premium,
            BattlegroundsNormalDbfId = normal,
            BattlegroundsSkinParentId = skinParent,
        };
    }

    private static Card NewPoolCard(BgCard b) => new()
    {
        Id = b.ExternalId,
        DbfId = b.Id,
        Name = b.Name,
        Text = b.Text,
        Cost = b.ManaCost,
        Attack = b.Attack,
        Health = b.Health,
        Type = b.IsMinion ? "MINION" : "SPELL",
        Set = "BATTLEGROUNDS",
        Races = MapRaces(b.MinionTypes),
        Mechanics = MapMechanics(b.Keywords),
        TechLevel = b.TechLevel,
        IsBattlegroundsPoolMinion = b.IsMinion,
        IsBattlegroundsPoolSpell = b.IsSpell,
        IsBattlegroundsDuosExclusive = b.IsDuosOnly,
        BattlegroundsPremiumDbfId = b.GoldenDbfId,
    };

    private static IReadOnlyList<string> MapRaces(IReadOnlyList<string> minionTypes)
    {
        if (minionTypes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var races = new List<string>(minionTypes.Count);
        foreach (var type in minionTypes)
        {
            races.Add(type.ToUpperInvariant());
        }

        return races;
    }

    private static IReadOnlyList<string> MapMechanics(IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return Array.Empty<string>();
        }

        var mechanics = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            mechanics.Add(keyword.ToUpperInvariant().Replace(' ', '_'));
        }

        return mechanics;
    }

    /// <summary>Maps a hero skin to its base hero so stats aggregate across skins.</summary>
    public string BaseHeroId(string cardId)
    {
        var card = Get(cardId);
        if (card is { BattlegroundsSkinParentId: > 0 } && Get(card.BattlegroundsSkinParentId) is { } parent)
        {
            return parent.Id;
        }

        var skin = cardId.IndexOf("_SKIN_", StringComparison.Ordinal);
        return skin > 0 ? cardId[..skin] : cardId;
    }

    /// <summary>The rune types of <c>runeCost</c> ({"blood": 1, "frost": 0, "unholy": 2}) with a count above 0, upper case.</summary>
    internal static IReadOnlyList<string> RunesOf(JsonElement e)
    {
        if (!e.TryGetProperty("runeCost", out var cost) || cost.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        var runes = new List<string>(3);
        foreach (var rune in cost.EnumerateObject())
        {
            if (rune.Value.ValueKind == JsonValueKind.Number && rune.Value.TryGetInt32(out var count) && count > 0)
            {
                runes.Add(rune.Name.ToUpperInvariant());
            }
        }

        return runes;
    }
}
