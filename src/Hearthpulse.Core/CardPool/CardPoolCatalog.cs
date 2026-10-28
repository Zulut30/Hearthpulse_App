using System.Text.Json;
using System.Text.Json.Serialization;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// Every collectible Constructed card with its format membership: the universe generator pools are cut from.
/// Main source: the Hearthpulse API (<c>/api/v1/constructed-cards?format=wild</c>, which also lists each card's
/// formats); fallback: HearthstoneJSON's English collectible cards with Standard decided by set
/// (<see cref="CardPoolTables.StandardSets"/>). Cached on disk as a compact list.
/// </summary>
public sealed class CardPoolCatalog
{
    public const string ApiSource = "Hearthpulse API";

    public const string HearthstoneJsonSource = "HearthstoneJSON";

    private const int PageSize = 200;

    // A Wild list is ~33 pages; more than this means the server stopped paging.
    private const int MaxPages = 80;

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The API's multi_class ids (CLASS tag values).
    private static readonly Dictionary<int, string> ClassIds = new()
    {
        [1] = "DEATHKNIGHT", [2] = "DRUID", [3] = "HUNTER", [4] = "MAGE", [5] = "PALADIN", [6] = "PRIEST",
        [7] = "ROGUE", [8] = "SHAMAN", [9] = "WARLOCK", [10] = "WARRIOR", [12] = "NEUTRAL", [14] = "DEMONHUNTER",
    };

    private readonly Dictionary<string, PoolCard> _byId;
    private readonly Dictionary<int, PoolCard> _byDbf;

    public CardPoolCatalog(IReadOnlyList<PoolCard> cards, string source, DateTime updated)
    {
        Cards = cards;
        Source = source;
        Updated = updated;
        _byId = new Dictionary<string, PoolCard>(StringComparer.Ordinal);
        _byDbf = new Dictionary<int, PoolCard>();
        foreach (var card in cards)
        {
            _byId[card.Id] = card;
            if (card.DbfId > 0)
            {
                _byDbf[card.DbfId] = card;
            }
        }
    }

    public static CardPoolCatalog Empty { get; } = new([], "none", DateTime.MinValue);

    public IReadOnlyList<PoolCard> Cards { get; }

    /// <summary><see cref="ApiSource"/> or <see cref="HearthstoneJsonSource"/>.</summary>
    public string Source { get; }

    public DateTime Updated { get; }

    public int Count => Cards.Count;

    public PoolCard? Get(string? cardId) => cardId is { Length: > 0 } && _byId.TryGetValue(cardId, out var card) ? card : null;

    public PoolCard? Get(int dbfId) => _byDbf.GetValueOrDefault(dbfId);

    /// <summary>
    /// The same cards with every minion type HearthstoneJSON knows: the API keeps one type per minion, so dual-type
    /// minions (Beast + Dragon...) would miss from one of their tribes' pools. Death Knight rune costs, which the API
    /// does not have at all ("Discover a Blood Rune card"), come from HearthstoneJSON the same way.
    /// </summary>
    public CardPoolCatalog WithRaces(CardDb cards)
    {
        if (cards.Count == 0)
        {
            return this;
        }

        var changed = false;
        var list = new List<PoolCard>(Cards.Count);
        foreach (var card in Cards)
        {
            var known = cards.Get(card.Id);
            var result = card;
            if (known is { Races.Count: > 1 } && !known.Races.All(card.Races.Contains))
            {
                result = result with { Races = known.Races.ToArray() };
            }

            if (known is { Runes.Count: > 0 } && !known.Runes.SequenceEqual(card.Runes))
            {
                result = result with { Runes = known.Runes };
            }

            changed |= !ReferenceEquals(result, card);
            list.Add(result);
        }

        return changed ? new CardPoolCatalog(list, Source, Updated) : this;
    }

    /// <summary>Loads the catalog: a fresh disk cache, else the API, else a stale cache, else HearthstoneJSON. Never throws.</summary>
    public static async Task<CardPoolCatalog> LoadAsync(HearthpulseApi api, HttpClient http, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(AppPaths.Cache, "api", "card-pool.json");
        var cached = ReadCache(file);
        if (cached is not null && DateTime.UtcNow - cached.Updated <= MaxAge)
        {
            return cached;
        }

        try
        {
            if (await FetchApiAsync(api, cancellationToken).ConfigureAwait(false) is { Count: > 0 } fresh)
            {
                var catalog = new CardPoolCatalog(fresh, ApiSource, DateTime.UtcNow);
                WriteCache(file, catalog);
                return catalog;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Diag.Error("Card pool: the API answer could not be read", ex);
        }

        if (cached is not null)
        {
            Diag.Info($"Card pool: using the cached list from {cached.Updated:u} ({cached.Count} cards)");
            return cached;
        }

        return await LoadHearthstoneJsonAsync(http, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every page of <c>/api/v1/constructed-cards</c>; null when a page fails.</summary>
    internal static async Task<List<PoolCard>?> FetchApiAsync(HearthpulseApi api, CancellationToken cancellationToken)
    {
        var cards = new List<PoolCard>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxPages; page++)
        {
            var result = await api.GetAsync($"api/v1/constructed-cards?format=wild&per_page={PageSize}&page={page}", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                Diag.Info($"Card pool: constructed-cards page {page}: {result.Error!.Code} {result.Error.Message}");
                return null;
            }

            // The last page is short, and a page past the end repeats the last one (checked 2026-10-28): a full last page
            // is followed by the same cards again, so a page with no new card ends the loop as well.
            var added = 0;
            foreach (var card in ParseApiPage(result.Data))
            {
                if (seen.Add(card.Id))
                {
                    cards.Add(card);
                    added++;
                }
            }

            if (result.Data.ValueKind != JsonValueKind.Array || result.Data.GetArrayLength() < PageSize || added == 0)
            {
                break;
            }
        }

        return cards;
    }

    /// <summary>Reads one page of <c>/api/v1/constructed-cards</c> (the <c>data</c> array).</summary>
    internal static IEnumerable<PoolCard> ParseApiPage(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var c in data.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || Str(c, "card_id") is not { Length: > 0 } id)
            {
                continue;
            }

            var classes = new List<string>();
            if (c.TryGetProperty("multi_class", out var multi) && multi.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in multi.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Number && ClassIds.TryGetValue(m.GetInt32(), out var name))
                    {
                        classes.Add(name);
                    }
                }
            }

            if (classes.Count == 0)
            {
                classes.Add(Str(c, "class") ?? "NEUTRAL");
            }

            var formats = c.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => Str(x, "slug")).ToList()
                : [];
            var type = c.TryGetProperty("card_type", out var t) && t.ValueKind == JsonValueKind.Object ? Str(t, "slug") ?? string.Empty : string.Empty;
            var race = Str(c, "minion_type");
            yield return new PoolCard(
                id,
                Int(c, "dbf") ?? 0,
                Localized(c, "name", "en") ?? id,
                PoolText.Normalize(Localized(c, "text", "en")),
                type,
                Str(c, "rarity") ?? string.Empty,
                Int(c, "mana_cost") ?? 0,
                Int(c, "attack"),
                type == "WEAPON" ? Int(c, "durability") : Int(c, "health"),
                classes,
                race is { Length: > 0 } ? [race] : [],
                Str(c, "spell_school"),
                Strings(c, "mechanics"),
                Strings(c, "referenced_tags"),
                Str(c, "card_set") ?? string.Empty,
                formats.Contains("standard"))
            {
                NameRu = Localized(c, "name", "ru"),
            };
        }
    }

    private static async Task<CardPoolCatalog> LoadHearthstoneJsonAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppPaths.Cache, "cards.enUS.collectible.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(2))
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync("https://api.hearthstonejson.com/v1/latest/enUS/cards.collectible.json", cancellationToken).ConfigureAwait(false);
                    // Only a card list replaces the file: a page from a captive portal or a cut-off body would be kept
                    // for two days and leave the pool empty. Written through a temp file, so a crash leaves the old one.
                    if (ParseHearthstoneJson(new MemoryStream(bytes)).Count > 0)
                    {
                        await File.WriteAllBytesAsync(path + ".tmp", bytes, cancellationToken).ConfigureAwait(false);
                        File.Move(path + ".tmp", path, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    Diag.Info($"Card pool: HearthstoneJSON answer unreadable ({ex.Message})");
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // A timeout comes as a cancellation: the stale file (or nothing) is used, as when offline.
                    Diag.Info($"Card pool: HearthstoneJSON not reachable ({ex.Message})");
                }
            }

            if (!File.Exists(path))
            {
                return Empty;
            }

            await using var stream = File.OpenRead(path);
            var cards = ParseHearthstoneJson(stream);
            Diag.Info($"Card pool: {cards.Count} cards from HearthstoneJSON (API unavailable)");
            return new CardPoolCatalog(cards, HearthstoneJsonSource, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Diag.Error("Card pool: HearthstoneJSON fallback failed", ex);
            return Empty;
        }
    }

    /// <summary>HearthstoneJSON cards (English): collectible, non-hero-skin Constructed cards.</summary>
    internal static List<PoolCard> ParseHearthstoneJson(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PoolCard>();
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            if (Str(c, "id") is not { Length: > 0 } id
                || !(c.TryGetProperty("collectible", out var col) && col.ValueKind == JsonValueKind.True)
                || Str(c, "set") is not { Length: > 0 } set || set is "HERO_SKINS" or "BATTLEGROUNDS" or "LETTUCE" or "VANILLA"
                || Str(c, "type") is not { Length: > 0 } type || type == "HERO" && Int(c, "cost") is null)
            {
                continue;
            }

            var classes = Strings(c, "classes");
            list.Add(new PoolCard(
                id,
                Int(c, "dbfId") ?? 0,
                Str(c, "name") ?? id,
                PoolText.Normalize(Str(c, "text")),
                type,
                Str(c, "rarity") ?? string.Empty,
                Int(c, "cost") ?? 0,
                Int(c, "attack"),
                type == "WEAPON" ? Int(c, "durability") : Int(c, "health"),
                classes.Count > 0 ? classes : [Str(c, "cardClass") ?? "NEUTRAL"],
                Strings(c, "races") is { Count: > 0 } races ? races : Str(c, "race") is { Length: > 0 } race ? [race] : [],
                Str(c, "spellSchool"),
                Strings(c, "mechanics"),
                Strings(c, "referencedTags"),
                set,
                CardPoolTables.StandardSets.Contains(set))
            {
                Runes = CardDb.RunesOf(c),
            });
        }

        return list;
    }

    private static CardPoolCatalog? ReadCache(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var stream = File.OpenRead(file);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            Diag.Error("Card pool cache unreadable", ex);
            return null;
        }
    }

    private static void WriteCache(string file, CardPoolCatalog catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            using (var stream = File.Create(temp))
            {
                catalog.Write(stream);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Card pool cache not written", ex);
        }
    }

    /// <summary>Reads a catalog written by <see cref="Write"/> (the disk cache, test fixtures).</summary>
    internal static CardPoolCatalog? Read(Stream stream)
    {
        var cache = JsonSerializer.Deserialize<CacheFile>(stream, CacheJson);
        return cache?.Cards is { Count: > 0 } cards ? new CardPoolCatalog(cards, cache.Source ?? ApiSource, cache.Updated) : null;
    }

    internal void Write(Stream stream) => JsonSerializer.Serialize(stream, new CacheFile(Source, Updated, Cards.ToList()), CacheJson);

    private sealed record CacheFile(string? Source, DateTime Updated, List<PoolCard>? Cards);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Localized(JsonElement e, string name, string lang) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? Str(v, lang) : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
