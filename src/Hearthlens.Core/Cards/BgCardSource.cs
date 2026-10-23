using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Hearthlens.Core.Cards;

/// <summary>
/// One card from the hsbg.cards community Battlegrounds database: tavern tier, stats, text, keywords,
/// golden/related ids and the site's image paths, keyed by Hearthstone card id (<see cref="ExternalId"/>).
/// </summary>
public sealed record BgCard(
    int Id,
    string Slug,
    string ExternalId,
    string Name,
    string Text,
    int ManaCost,
    int Attack,
    int Health,
    int TechLevel,
    string CardType,
    string SpellSchool,
    IReadOnlyList<string> MinionTypes,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Categories,
    bool IsPool,
    bool IsTimewarped,
    bool IsHero,
    bool IsHeroSkin,
    bool IsDuosOnly,
    int GoldenDbfId,
    int ParentId,
    string Image,
    string ImageGold)
{
    /// <summary>A tavern-pool card: current-patch pool minion or tavern spell, not a token.</summary>
    public bool IsPoolCard => Categories.Contains("tavern") && TechLevel > 0
                              && !IsTimewarped && !IsHero && !IsHeroSkin && !Categories.Contains("token");

    public bool IsMinion => string.Equals(CardType, "minion", StringComparison.OrdinalIgnoreCase);

    public bool IsSpell => string.Equals(CardType, "spell", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Battlegrounds card metadata from hsbg.cards (community project, anonymous API, 120 requests/minute).
/// Supplements HearthstoneJSON: fills missing tavern tiers, text, races, keywords and golden ids, and adds
/// current-patch pool cards the card feed does not know yet. Cached on disk; never throws.
/// </summary>
public sealed class BgCardDb
{
    public const string Attribution = "BG card data: hsbg.cards";

    private const string Root = "https://hsbg.cards/";
    private const string ListUrl = "https://hsbg.cards/api/v1/cards?limit={0}&offset={1}&pool=current";
    private const int PageSize = 100;
    private const int MaxPages = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    private readonly Dictionary<string, BgCard> _byExternalId;
    private readonly Dictionary<int, BgCard> _byId;

    private BgCardDb(IReadOnlyList<BgCard> cards)
    {
        Cards = cards;
        _byExternalId = new Dictionary<string, BgCard>(StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<int, BgCard>();
        foreach (var card in cards)
        {
            _byExternalId[card.ExternalId] = card;
            if (card.Id > 0)
            {
                _byId[card.Id] = card;
            }
        }
    }

    public static BgCardDb Empty { get; } = new(Array.Empty<BgCard>());

    public IReadOnlyList<BgCard> Cards { get; }

    public int Count => Cards.Count;

    public BgCard? Get(string? cardId) =>
        cardId is { Length: > 0 } && _byExternalId.TryGetValue(cardId, out var card) ? card : null;

    public BgCard? Get(int dbfId) => _byId.GetValueOrDefault(dbfId);

    /// <summary>Absolute URL of the site's card image (prefers the normal art, then the golden art).</summary>
    public string? ImageUrl(string? cardId, bool golden = false)
    {
        if (Get(cardId) is not { } card)
        {
            return null;
        }

        var path = golden ? card.ImageGold.Length > 0 ? card.ImageGold : card.Image : card.Image;
        return path.Length == 0 ? null : Root.TrimEnd('/') + path;
    }

    /// <summary>Loads from cache, refreshing from hsbg.cards when stale. Never throws.</summary>
    public static async Task<BgCardDb> LoadAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AppPaths.Cache, "bgcards.hsbg.json");
        var file = new FileInfo(path);
        if (!file.Exists || DateTime.UtcNow - file.LastWriteTimeUtc > MaxAge)
        {
            await RefreshAsync(http, path, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Diag.Error("BG card supplement unreadable", ex);
            return Empty;
        }
    }

    private static async Task RefreshAsync(HttpClient http, string path, CancellationToken cancellationToken)
    {
        var items = new List<string>();
        var offset = 0;
        var total = int.MaxValue;
        var complete = false;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = string.Format(ListUrl, PageSize, offset);
            using var doc = await GetJsonAsync(http, url, cancellationToken).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                items.Add(item.GetRawText());
                count++;
            }

            if (doc.RootElement.TryGetProperty("pagination", out var pagination))
            {
                if (pagination.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    total = t.GetInt32();
                }

                if (pagination.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number)
                {
                    offset = next.GetInt32();
                }
                else
                {
                    offset += count;
                }
            }
            else
            {
                offset += count;
            }

            if (count < PageSize || offset >= total)
            {
                complete = true;
                break;
            }
        }

        // A partial page set would poison the cache for half a day: keep the previous copy instead.
        if (items.Count == 0 || (!complete && File.Exists(path)))
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes("[" + string.Join(',', items) + "]");
            await File.WriteAllBytesAsync(path + ".tmp", bytes, cancellationToken).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            Diag.Info($"BG card supplement refreshed ({items.Count} cards)");
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException)
        {
            Diag.Error("BG card supplement not saved", ex);
        }
    }

    private static async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Diag.Error($"BG card supplement download failed ({url}): {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            Diag.Error($"BG card supplement download failed ({url})", ex);
            return null;
        }
    }

    /// <summary>
    /// Parses the cached bulk array, a paged <c>{"data":[...]}</c> response, or a single <c>{"data":{...}}</c>
    /// card response. Malformed entries are skipped.
    /// </summary>
    public static BgCardDb Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new List<BgCard>();
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                ParseItems(root, result);
                break;
            case JsonValueKind.Object when root.TryGetProperty("data", out var data):
                if (data.ValueKind == JsonValueKind.Array)
                {
                    ParseItems(data, result);
                }
                else if (data.ValueKind == JsonValueKind.Object && ParseCard(data, out var single))
                {
                    result.Add(single);
                }

                break;
        }

        return result.Count == 0 ? Empty : new BgCardDb(result);
    }

    private static void ParseItems(JsonElement array, List<BgCard> result)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && ParseCard(item, out var card))
            {
                result.Add(card);
            }
        }
    }

    private static bool ParseCard(JsonElement e, out BgCard card)
    {
        card = null!;
        var externalId = Str(e, "externalId").Trim();
        var id = Int(e, "id");
        if (externalId.Length == 0)
        {
            return false;
        }

        card = new BgCard(
            id,
            Str(e, "slug"),
            externalId,
            Str(e, "name").Trim(),
            Str(e, "text"),
            Int(e, "manaCost"),
            Int(e, "attack"),
            Int(e, "health"),
            Int(e, "tier"),
            Str(e, "cardType"),
            Str(e, "spellSchool"),
            StrArray(e, "minionTypes"),
            StrArray(e, "keywords"),
            StrArray(e, "categories"),
            Bool(e, "pool"),
            Bool(e, "isTimewarped"),
            Bool(e, "isHero"),
            Bool(e, "isHeroSkin"),
            Bool(e, "isDuosOnly"),
            Int(e, "dbfIdGold"),
            Int(e, "parentId"),
            Str(e, "image"),
            Str(e, "imageGold"));
        return true;
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
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }

        return list;
    }
}
