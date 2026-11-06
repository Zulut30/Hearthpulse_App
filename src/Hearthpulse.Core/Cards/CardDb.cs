using System.Text.Json;

namespace Hearthpulse.Core.Cards;

/// <summary>
/// Card metadata from HearthstoneJSON, supplemented by hsbg.cards (tavern tiers, text and current-patch pool
/// cards) and cached on disk per locale.
/// </summary>
public sealed partial class CardDb
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
            var stream = File.OpenRead(path);
            await using var streamScope = stream.ConfigureAwait(false);
            return ParseList(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Card data unreadable", ex);
            return [];
        }
    }

    public static CardDb Parse(string locale, Stream json) => new(locale, ParseList(json));

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
