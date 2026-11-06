using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Api;

/// <summary>
/// Community stats from the Hearthpulse API: Battlegrounds heroes and minions per MMR slice, and every
/// ranked Constructed number the app shows (HSGuru meta and decks, HSReplay archetypes, mulligan and matchups).
/// Answers are cached on disk, and a stale cache is used when the server is down. Never throws.
/// </summary>
/// <param name="cacheDirectory">Where answers are cached; the app's cache folder by default (tests pass a scratch one).</param>
public sealed partial class HearthpulseData(HearthpulseApi api, string? cacheDirectory = null)
{
    /// <summary>The name every number from the Hearthpulse API goes by in the UI, in both languages.</summary>
    public const string Attribution = "Hearthpulse data";

    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>How old a real answer may be and still stand in for a 404 (a slice the server dropped for a while).</summary>
    private static readonly TimeSpan MaxStaleForMissing = TimeSpan.FromDays(2);

    /// <summary>Values from answers that become part of a URL or cache file name must look like the API's own keys.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9_.]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SafeKey { get; }

    /// <summary>The longest archetype name sent in a query; a longer one is not a real archetype.</summary>
    private const int MaxArchetypeLength = 120;

    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");

    /// <summary>MMR slices the API serves for heroes (<c>/api/bg/heroes</c>); it has no all-ranks slice.</summary>
    private static readonly HashSet<string> HeroMmrs = new(StringComparer.Ordinal) { "TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT" };

    /// <summary>MMR slices the API serves for minions (<c>/v1/battlegrounds/minions/stats/slices</c>).</summary>
    private static readonly HashSet<string> MinionMmrs = new(StringComparer.Ordinal) { "ALL", "TOP_50_PERCENT", "TOP_20_PERCENT" };

    /// <summary>
    /// Solo hero results of one MMR slice (current Battlegrounds patch), by hero card id, with the time the server fetched
    /// them. Sample sizes are not published: DataPoints is 0. Empty for a slice the API does not have.
    /// </summary>
    public async Task<BgHeroSlice> LoadHeroesAsync(string mmr = "TOP_50_PERCENT", CancellationToken cancellationToken = default)
    {
        var empty = new BgHeroSlice(new Dictionary<string, MetaHero>(), default);
        if (!HeroMmrs.Contains(mmr))
        {
            return empty;
        }

        using var doc = await FetchAsync($"api/bg/heroes?mode=solo&mmr={mmr}", $"bg-heroes-{mmr.ToLowerInvariant()}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? empty : Read(() => ParseHeroSlice(Data(doc)), "heroes", empty);
    }

    /// <summary>
    /// Minion results of one MMR slice ("ALL" = all ranks, current Battlegrounds patch), by card id: average final
    /// placement of players who had the minion and how many games that is. No per-turn results.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MetaCard>> LoadMinionsAsync(string mmr = "ALL", CancellationToken cancellationToken = default)
    {
        if (!MinionMmrs.Contains(mmr))
        {
            return new Dictionary<string, MetaCard>();
        }

        using var doc = await FetchAsync($"v1/battlegrounds/minions/stats?mmr={mmr}&period=CURRENT_BATTLEGROUNDS_PATCH&limit=500", $"bg-minions-{mmr.ToLowerInvariant()}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? new Dictionary<string, MetaCard>() : Read(() => ParseMinions(Data(doc)), "minions", new Dictionary<string, MetaCard>());
    }

    /// <summary>Ranked archetypes for a format ("standard"/"wild") and an app rank key ("legend", "all", ...).</summary>
    public async Task<IReadOnlyList<MetaArchetype>> LoadArchetypesAsync(string format, string rank, CancellationToken cancellationToken = default)
    {
        var gameType = format == "wild" ? "RANKED_WILD" : "RANKED_STANDARD";
        var rankRange = RankRange(rank);
        using var doc = await FetchAsync($"api/db/archetypes?game_type={gameType}&rank_range={rankRange}&limit=200", $"archetypes-{gameType}-{rankRange}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseArchetypes(Data(doc)), "archetypes", new List<MetaArchetype>());
    }

    /// <summary>An archetype's HSReplay decklists (Standard Legend), for matching the player's deck to an archetype.</summary>
    public async Task<IReadOnlyList<GuideDeck>> LoadArchetypeDecksAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/decks?limit=200", $"archetype-{archetypeId}-decks.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseArchetypeDecks(Data(doc)), "archetype decks", new List<GuideDeck>());
    }

    /// <summary>Every card's mulligan numbers for an archetype (Standard Legend), not only the rows HSReplay displays.</summary>
    public async Task<IReadOnlyList<GuideCard>> LoadArchetypeMulliganAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/mulligan?display_only=false&limit=250", $"archetype-{archetypeId}-mulligan.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseMulligan(Data(doc)), "archetype mulligan", new List<GuideCard>());
    }

    /// <summary>
    /// A generator's Hearthstone Wiki pools (<c>wiki.generated_card_pools</c>); null when the API has no answer (offline,
    /// unknown card, an id that is not a card id).
    /// </summary>
    public async Task<CardPool.CardWiki?> LoadCardWikiAsync(string cardId, CancellationToken cancellationToken = default)
    {
        if (!CardIdKey.IsMatch(cardId))
        {
            return null;
        }

        using var doc = await FetchAsync($"api/v1/constructed-cards/{cardId}/wiki", $"card-wiki-{cardId}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => CardPool.WikiPools.Parse(Data(doc)), "card wiki", (CardPool.CardWiki?)null);
    }

    /// <summary>Card ids that may go into a URL and a cache file name.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_]{1,48}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CardIdKey { get; }

    public async Task<IReadOnlyList<GuideMatchup>> LoadArchetypeMatchupsAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/matchups?limit=500", $"archetype-{archetypeId}-matchups.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseMatchups(Data(doc)), "archetype matchups", new List<GuideMatchup>());
    }

    /// <summary>
    /// HSGuru's meta for one format, rank and period (keys as the API takes them, e.g. "standard", "legend",
    /// "past_week"), with the periods the API offers. Null when neither the server nor the cache has it.
    /// </summary>
    public async Task<HsguruMetaSlice?> LoadHsguruMetaAsync(string format, string rank, string period, CancellationToken cancellationToken = default)
    {
        // The period can come from an earlier answer ("current_period"): only key-like values reach the URL and the file name.
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || !SafeKey.IsMatch(period))
        {
            return null;
        }

        using var doc = await FetchAsync($"v1/hsguru/meta?format={format}&rank={rank}&period={Uri.EscapeDataString(period)}&min_games=100", $"hsguru-meta-{format}-{rank}-{period}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read<HsguruMetaSlice?>(() => ParseHsguruMeta(doc.RootElement), "HSGuru meta", null);
    }

    /// <summary>Every HSGuru archetype of a format (current expansion, all ranks) with its most played decklists.</summary>
    public async Task<IReadOnlyList<HsguruArchetype>> LoadHsguruArchetypesAsync(string format, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"v1/hsguru/archetypes?format={format}&min_games=50&limit=500", $"hsguru-archetypes-{format}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseHsguruArchetypes(Data(doc)), "HSGuru archetypes", new List<HsguruArchetype>());
    }

    /// <summary>
    /// HSGuru's per-card mulligan numbers and class matchups for one archetype (Legend, past week): the mulligan source
    /// for Wild and for archetypes HSReplay does not have. Null when the API has no analysis for it.
    /// </summary>
    public async Task<HsguruAnalysis?> LoadHsguruAnalysisAsync(string archetype, string format, CancellationToken cancellationToken = default)
    {
        if (!SafeKey.IsMatch(format) || archetype.Length is 0 or > MaxArchetypeLength)
        {
            return null;
        }

        var file = $"hsguru-analysis-{format}-{ApiConstructedMeta.StableId(archetype):x8}.json";
        using var doc = await FetchAsync($"v1/hsguru/archetypes/analysis?archetype={Uri.EscapeDataString(archetype)}&format={format}", file, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read<HsguruAnalysis?>(() => ParseHsguruAnalysis(Data(doc)), "HSGuru analysis", null);
    }

    /// <summary>
    /// One HSGuru mulligan slice of an archetype (current expansion): a rank ("legend", "all", ...), the coin
    /// ("any_player", "going_first", "on_coin") and the opponent's class ("any" or "WARLOCK"). The server keeps the coin
    /// and the class apart, so a slice sets at most one of them. Null when the server has not collected the slice (a 404,
    /// remembered like any answer).
    /// </summary>
    public async Task<MulliganSlice?> LoadHsguruMulliganAsync(string archetype, string format, string rank, string coin, string opponentClass, CancellationToken cancellationToken = default)
    {
        var opponent = opponentClass.ToLowerInvariant();
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || !SafeKey.IsMatch(coin) || !SafeKey.IsMatch(opponent) || archetype.Length is 0 or > MaxArchetypeLength)
        {
            return null;
        }

        var server = opponent == MulliganGuide.AnyOpponent ? opponent : opponent.ToUpperInvariant();
        var file = $"hsguru-mulligan-{format}-{ApiConstructedMeta.StableId(archetype):x8}-{rank}-{coin}-{opponent}.json";
        using var doc = await FetchAsync($"v1/hsguru/mulligan?archetype={Uri.EscapeDataString(archetype)}&format={format}&rank={rank}&coin={coin}&opponent_class={server}",
            file, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => ParseHsguruMulligan(Data(doc)), "HSGuru mulligan", (MulliganSlice?)null);
    }

    /// <summary>
    /// HSGuru's results of an archetype against each class (current expansion, all players) at one rank ("legend",
    /// "all", ...): the mulligan guide's class shift when HSReplay has no matchups for the archetype. Empty when the
    /// server has not collected it (a 404, remembered like any answer).
    /// </summary>
    public async Task<IReadOnlyList<GuideMatchup>> LoadHsguruClassMatchupsAsync(string archetype, string format, string rank, CancellationToken cancellationToken = default)
    {
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || archetype.Length is 0 or > MaxArchetypeLength)
        {
            return [];
        }

        var file = $"hsguru-class-matchups-{format}-{ApiConstructedMeta.StableId(archetype):x8}-{rank}.json";
        using var doc = await FetchAsync($"v1/hsguru/class-matchups?archetype={Uri.EscapeDataString(archetype)}&format={format}&rank={rank}", file, cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseHsguruClassMatchups(Data(doc)), "HSGuru class matchups", new List<GuideMatchup>());
    }

    /// <summary>
    /// The ranks <c>v1/hsguru/matchups</c> takes; the meta's narrower ones (top 500, top 100) read its top-Legend slice, and
    /// Diamond (the API answers 422 for it) its Diamond 4-1 slice, which the meta page names under the heat map.
    /// </summary>
    internal static string MatchupRank(string rank) => rank switch
    {
        "all" or "top_legend" or "top_5k" or "legend" or "diamond_to_legend" or "diamond_4to1" or "bronze_plat" => rank,
        "diamond" => "diamond_4to1",
        "top_500" or "top_100" => "top_legend",
        _ => "all",
    };

    /// <summary>
    /// HSGuru's matchup matrix of the <paramref name="limit"/> most played archetypes of a format, rank and period (an API
    /// period such as "past_week" or "black_empire"). Null when neither the server nor the cache has it.
    /// </summary>
    public async Task<(IReadOnlyList<MatchupArchetype> Archetypes, IReadOnlyList<MatchupCell> Cells, int Games)?> LoadHsguruMatchupsAsync(
        string format, string rank, string period, int limit = 12, CancellationToken cancellationToken = default)
    {
        var server = MatchupRank(rank);
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(period) || limit is < 2 or > 40)
        {
            return null;
        }

        using var doc = await FetchAsync($"v1/hsguru/matchups?format={format}&rank={server}&period={Uri.EscapeDataString(period)}&limit={limit}",
            $"hsguru-matchups-{format}-{server}-{period}-{limit}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => ParseHsguruMatchups(Data(doc)), "HSGuru matchups", ((IReadOnlyList<MatchupArchetype>, IReadOnlyList<MatchupCell>, int)?)null);
    }

    /// <param name="shrink">Keeps only what the app reads of a large answer, so the cache file stays small.</param>
    /// <param name="retry">Set when asking again after the cached file could not be read.</param>
    private async Task<JsonDocument?> FetchAsync(string path, string cacheName, CancellationToken cancellationToken, Func<JsonElement, object>? shrink = null,
        bool retry = false)
    {
        var file = Path.Combine(_cacheDirectory, cacheName);
        var info = new FileInfo(file);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            var result = await api.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                // "meta" (periods, fetch time) is kept next to "data" when the endpoint sends it.
                var data = shrink is null ? (object)result.Data : shrink(result.Data);
                var text = result.Meta.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.Serialize(new { data })
                    : JsonSerializer.Serialize(new { data, meta = result.Meta });
                // The whole answer is kept (not only "data"): some endpoints put their list next to it.
                await WriteCacheAsync(file, text, cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(text);
            }

            Diag.Info($"Hearthpulse API {path}: {result.Error!.Code} {result.Error.Message}");
            if (result.Error.Code is "NOT_AVAILABLE" or "HTTP_422")
            {
                // The server has nothing for this (404), or does not serve this filter combination (422): remember that for as long as an answer, instead of asking every
                // load. A real answer from the last two days is used instead of being replaced by "nothing" (it keeps its
                // own time, so it is asked again on later loads and gives way after two days).
                if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxStaleForMissing || !HoldsData(file))
                {
                    await WriteCacheAsync(file, "{\"data\":null}", cancellationToken).ConfigureAwait(false);
                    return null;
                }
            }
        }

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Null)
            {
                doc.Dispose();
                return null;
            }

            return doc;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Diag.Error($"Hearthpulse API cache unreadable ({cacheName})", ex);
            if (ex is JsonException && !retry && TryDelete(file))
            {
                // A young file that cannot be read (zeros after a power cut) kept the API from being asked for six hours.
                return await FetchAsync(path, cacheName, cancellationToken, shrink, retry: true).ConfigureAwait(false);
            }

            return null;
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a cache file through a temporary file, so a reader (another load, the mulligan guide) never sees half of it
    /// and a crash mid-write leaves the old answer.
    /// </summary>
    internal static async Task WriteCacheAsync(string file, string text, CancellationToken cancellationToken)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Hearthpulse API cache not written ({Path.GetFileName(file)})", ex);
        }
        finally
        {
            // A cancelled or failed write leaves no half-written temporary file behind (after the move there is none).
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>True when the cache file is a real answer, not the remembered "nothing" of a 404.</summary>
    private static bool HoldsData(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind != JsonValueKind.Null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The list an endpoint returned, wrapped by <see cref="FetchAsync"/> as <c>{"data": answer}</c>.</summary>
    private static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");

    private static T Read<T>(Func<T> parse, string section, T empty)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Diag.Error($"Hearthpulse API data unreadable ({section})", ex);
            return empty;
        }
    }
}

/// <summary>One MMR slice of hero results and when the server fetched it (default when unknown).</summary>
public sealed record BgHeroSlice(IReadOnlyDictionary<string, MetaHero> Heroes, DateTime FetchedAt);
