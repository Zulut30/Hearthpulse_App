using System.Net.Http;
using System.Text.Json;

namespace Hearthlens.Core.Cards;

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
        _byId = new Dictionary<string, Card>(StringComparer.OrdinalIgnoreCase);
        _byDbf = new Dictionary<int, Card>();
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
    public static async Task<CardDb> LoadAsync(string locale, HttpClient http, CancellationToken cancellationToken = default)
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
                using (JsonDocument.Parse(bytes))
                {
                }

                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
                Diag.Info($"Card data refreshed ({locale}, {bytes.Length:N0} bytes)");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
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
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diag.Error("Card data unreadable", ex);
            return [];
        }
    }

    public static CardDb Parse(string locale, Stream json) => new(locale, ParseList(json));

    internal static List<Card> ParseList(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var cards = new List<Card>(doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = Str(e, "id");
            if (id.Length == 0)
            {
                continue;
            }

            cards.Add(new Card
            {
                Id = id,
                DbfId = Int(e, "dbfId"),
                Name = Str(e, "name"),
                Text = Str(e, "text"),
                Cost = Int(e, "cost"),
                Attack = Int(e, "attack"),
                Health = Int(e, "health"),
                Armor = Int(e, "armor"),
                Type = Str(e, "type"),
                Rarity = Str(e, "rarity"),
                CardClass = Str(e, "cardClass"),
                Set = Str(e, "set"),
                Races = StrArray(e, "races") is { Count: > 0 } races ? races : Str(e, "race") is { Length: > 0 } race ? [race] : Array.Empty<string>(),
                Mechanics = StrArray(e, "mechanics"),
                Collectible = Bool(e, "collectible"),
                TechLevel = Int(e, "techLevel"),
                IsBattlegroundsPoolMinion = Bool(e, "isBattlegroundsPoolMinion"),
                IsBattlegroundsPoolSpell = Bool(e, "isBattlegroundsPoolSpell"),
                IsBattlegroundsHero = Bool(e, "battlegroundsHero"),
                IsBattlegroundsDuosExclusive = Bool(e, "isBattlegroundsDuosExclusive"),
                BattlegroundsPremiumDbfId = Int(e, "battlegroundsPremiumDbfId"),
                BattlegroundsNormalDbfId = Int(e, "battlegroundsNormalDbfId"),
                BattlegroundsSkinParentId = Int(e, "battlegroundsSkinParentId"),
            });
        }

        return cards;
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

        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byDbf = new Dictionary<int, int>();
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

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> StrArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}
