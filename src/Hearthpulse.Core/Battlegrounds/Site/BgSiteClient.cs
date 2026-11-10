using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.Battlegrounds.Site;

/// <summary>What an answer of the service holds, which decides how it is checked and read.</summary>
internal enum BgSitePartKind
{
    TierLists,
    Heroes,
    HeroSlice,
    MinionStats,
    Comps,
    TrinketList,
}

/// <summary>One address of the service and its cache file (<c>bgsite-&lt;Name&gt;.json</c>).</summary>
internal sealed record BgSitePart(string Name, string Path, BgSitePartKind Kind);

/// <summary>
/// The Battlegrounds lists hearthpulse.net shows, read from the open service the site itself proxies
/// (docs/bg-site-stats.md): tier lists of heroes, minions and trinkets, minion numbers and comps, for the main
/// window's tabs. Only <c>GET</c>, no sign-in and nothing about the player in a request; five requests a refresh,
/// every one of them used. Other MMR slices are read on demand. Answers are cached on disk for <see cref="MaxAge"/>;
/// an old file stands in when the server fails or answers without data. Only data whose source is on the allow list
/// is read or kept.
/// </summary>
/// <param name="http">A client that sends no cookies and follows no redirects: <see cref="CreateHttp"/>.</param>
/// <param name="baseUrl">The service's address; <see cref="DefaultBaseUrl"/> unless it is another <c>https://</c> one.</param>
/// <param name="cacheDirectory">Where answers are cached; the app's API cache folder by default (tests pass a scratch one).</param>
public sealed class BgSiteClient(HttpClient http, string? baseUrl = null, string? cacheDirectory = null)
{
    public const string DefaultBaseUrl = "https://bg.hs-manacost.ru/";

    /// <summary>How long an answer is kept before the service is asked again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>Solo heroes: four slices; <see cref="BgSiteStats.Heroes"/> is the default one (top 50%).</summary>
    public static BgSiteSlices HeroesSoloSlices { get; } = new(["TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT"], "TOP_50_PERCENT");

    /// <summary>Duos heroes: the two slices checked against the service; <see cref="BgSiteStats.HeroesDuos"/> is the default one (top 50%).</summary>
    public static BgSiteSlices HeroesDuosSlices { get; } = new(["TOP_50_PERCENT", "TOP_20_PERCENT"], "TOP_50_PERCENT");

    /// <summary>Trinkets (last 7 days): four slices; <see cref="BgSiteStats.Trinkets"/> is the default one (top 1%).</summary>
    public static BgSiteSlices TrinketSlices { get; } = new(["TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT"], "TOP_1_PERCENT");

    /// <summary>One request's time limit.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The largest answer is under 1 MB; anything past this is not one of the lists.</summary>
    internal const int MaxBytes = 8 * 1024 * 1024;

    private static readonly BgSitePart TierLists = new("tier-lists", "api/bg/tier-lists", BgSitePartKind.TierLists);
    private static readonly BgSitePart Heroes = new("heroes", "api/bg/heroes", BgSitePartKind.Heroes);
    private static readonly BgSitePart MinionStats = new("minion-stats", "api/bg/library/minion-stats", BgSitePartKind.MinionStats);
    private static readonly BgSitePart Comps = new("comps", "api/bg-comps", BgSitePartKind.Comps);

    private readonly Uri _base = BaseAddress(baseUrl);
    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");

    /// <summary>Now (UTC), for the age of the cache files; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Counts <see cref="Timeout"/>; tests pass a clock they move.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The slices of the heroes list of a mode.</summary>
    public static BgSiteSlices HeroSlices(bool duos) => duos ? HeroesDuosSlices : HeroesSoloSlices;

    /// <summary>
    /// The client the hosts pass in: no cookies kept or sent, no redirects followed (an answer from anywhere but the
    /// service's own host is not read), nothing identifying in the headers.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The handler is owned by the HttpClient (disposeHandler: true), which lives as long as the app.")]
    public static HttpClient CreateHttp() => new(CreateHandler()) { Timeout = TimeSpan.FromSeconds(40) };

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// The refresh: every list in its default slice. Never throws. Null when no part could be had, from the service or
    /// the cache. Parts load independently: a part that failed is null or empty in the result, the others are there.
    /// Runs off the caller's thread.
    /// </summary>
    public Task<BgSiteStats?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), CancellationToken.None);

    /// <summary>
    /// The heroes list of one mode and MMR slice (one of <see cref="HeroSlices"/>), cached per mode and slice. The
    /// default solo slice is the list of the refresh. Null means "no data for the slice": the service does not have it,
    /// it could not be had, or the heroes answer names no allowed source to vouch for it. Never throws.
    /// </summary>
    public Task<BgSiteTierList?> LoadHeroesAsync(bool duos, string mmr, CancellationToken cancellationToken = default) =>
        HeroSlices(duos).Has(mmr)
            ? Task.Run(() => HeroesAsync(duos, mmr, cancellationToken), CancellationToken.None)
            : Task.FromResult<BgSiteTierList?>(null);

    /// <summary>
    /// The trinket tier list of one MMR slice (one of <see cref="TrinketSlices"/>; the last 7 days), cached per slice.
    /// The default slice is the list of the refresh. Null means "no data for the slice". Never throws.
    /// </summary>
    public Task<BgSiteTierList?> LoadTrinketsAsync(string mmr, CancellationToken cancellationToken = default) =>
        TrinketSlices.Has(mmr)
            ? Task.Run(() => TrinketsAsync(mmr, cancellationToken), CancellationToken.None)
            : Task.FromResult<BgSiteTierList?>(null);

    private static BgSitePart HeroSlice(bool duos, string mmr)
    {
        var mode = duos ? "duos" : "solo";
        return new BgSitePart($"heroes-{mode}-{mmr.ToLowerInvariant()}", $"api/bg/heroes?mode={mode}&mmr={mmr}", BgSitePartKind.HeroSlice);
    }

    private async Task<BgSiteTierList?> HeroesAsync(bool duos, string mmr, CancellationToken cancellationToken)
    {
        var heroes = PartAsync(Heroes, cancellationToken);
        if (!duos && mmr == HeroesSoloSlices.Default)
        {
            // The default solo slice is the site's own tier list, read as the refresh reads it.
            using var lists = (await PartAsync(TierLists, cancellationToken).ConfigureAwait(false)).Document;
            using var details = (await heroes.ConfigureAwait(false)).Document;
            return lists is null || details is null
                ? null
                : BgSiteParser.ParseTierList(lists.RootElement, BgSiteList.Heroes, BgSiteParser.ParseHeroes(details.RootElement));
        }

        var (slice, source) = await SliceAsync(HeroSlice(duos, mmr), heroes, cancellationToken).ConfigureAwait(false);
        using (slice)
        {
            using var held = (await heroes.ConfigureAwait(false)).Document;
            return slice is null ? null : BgSiteParser.ParseHeroSlice(slice.RootElement, HeroSlice(duos, mmr).Name, source!);
        }
    }

    private async Task<BgSiteTierList?> TrinketsAsync(string mmr, CancellationToken cancellationToken)
    {
        if (mmr == TrinketSlices.Default)
        {
            using var lists = (await PartAsync(TierLists, cancellationToken).ConfigureAwait(false)).Document;
            return lists is null ? null : BgSiteParser.ParseTierList(lists.RootElement, BgSiteList.Trinkets, heroes: null);
        }

        var part = new BgSitePart($"trinkets-{mmr.ToLowerInvariant()}", $"api/bg/tier-lists?list=trinkets&mmr={mmr}&timeRange=LAST_7_DAYS", BgSitePartKind.TrinketList);
        using var list = (await PartAsync(part, cancellationToken).ConfigureAwait(false)).Document;
        return list is null ? null : BgSiteParser.ParseList(list.RootElement, BgSiteList.Trinkets, heroes: null, "trinkets");
    }

    /// <summary>
    /// A heroes slice, asked for only after the answer of the same address without parameters has named an allowed
    /// source: the slice itself names none (docs/bg-site-stats.md, section 4). When that answer is missing, names no
    /// source or a refused one, the slice is neither requested nor read from the cache.
    /// </summary>
    private async Task<(JsonDocument? Slice, string? Source)> SliceAsync(BgSitePart slice, Task<Answer> heroes, CancellationToken cancellationToken)
    {
        var answer = await heroes.ConfigureAwait(false);
        var source = answer is { Refused: false, Document: { } document } ? BgSiteParser.HeroesSource(document.RootElement) : null;
        if (source is null)
        {
            Diag.Info($"Battlegrounds site {slice.Name}: not read, the heroes answer names no allowed source");
            return (null, null);
        }

        // The old heroes file after a failed load vouches for the old slice file only, not for a slice fetched now.
        return answer.Stale
            ? (await ReadCacheAsync(slice, CacheFile(slice)).ConfigureAwait(false), source)
            : ((await PartAsync(slice, cancellationToken).ConfigureAwait(false)).Document, source);
    }

    private async Task<BgSiteStats?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var lists = PartAsync(TierLists, cancellationToken);
        var heroes = PartAsync(Heroes, cancellationToken);
        var duos = SliceAsync(HeroSlice(duos: true, HeroesDuosSlices.Default), heroes, cancellationToken);
        var minions = PartAsync(MinionStats, cancellationToken);
        var comps = PartAsync(Comps, cancellationToken);

        JsonDocument?[] answers =
        [
            (await lists.ConfigureAwait(false)).Document,
            (await heroes.ConfigureAwait(false)).Document,
            (await duos.ConfigureAwait(false)).Slice,
            (await minions.ConfigureAwait(false)).Document,
            (await comps.ConfigureAwait(false)).Document,
        ];
        try
        {
            return Array.TrueForAll(answers, a => a is null)
                ? null
                : Build(Root(0), Root(1), Root(2), (await duos.ConfigureAwait(false)).Source, Root(3), Root(4));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer?.Dispose();
            }
        }

        JsonElement Root(int index) => answers[index]?.RootElement ?? default;
    }

    /// <summary>The records from the answers; an answer that is missing has <see cref="JsonValueKind.Undefined"/>.</summary>
    internal static BgSiteStats Build(JsonElement tierLists, JsonElement heroes, JsonElement heroesDuos, string? heroesSource, JsonElement minionStats, JsonElement comps)
    {
        var details = BgSiteParser.ParseHeroes(heroes);
        var heroList = BgSiteParser.ParseTierList(tierLists, BgSiteList.Heroes, details);
        var duosList = heroesDuos.ValueKind == JsonValueKind.Undefined || heroesSource is null
            ? null
            : BgSiteParser.ParseHeroSlice(heroesDuos, "heroes-duos", heroesSource);
        var minions = BgSiteParser.ParseTierList(tierLists, BgSiteList.Minions, heroes: null);
        var spells = BgSiteParser.ParseTierList(tierLists, BgSiteList.Spells, heroes: null);
        var trinkets = BgSiteParser.ParseTierList(tierLists, BgSiteList.Trinkets, heroes: null);
        var (stats, run) = BgSiteParser.ParseMinionStats(minionStats);
        var (compList, compsAt) = BgSiteParser.ParseComps(comps);
        compsAt = compList.Count > 0 ? compsAt : null;

        var times = new List<DateTime?> { heroList?.FetchedAt, duosList?.FetchedAt, minions?.FetchedAt, spells?.FetchedAt, trinkets?.FetchedAt, run?.CompletedAt, compsAt };
        return new BgSiteStats(heroList, duosList, minions, spells, trinkets, stats,
            new Dictionary<string, BgSiteCardStat>(StringComparer.Ordinal), compList, run, times.Max() ?? default, compsAt);
    }

    /// <summary>
    /// What <see cref="PartAsync"/> has for an address. <see cref="Refused"/>: the service answered just now and named
    /// a source that is not allowed. <see cref="Stale"/>: the load failed, the document, if any, is the old file.
    /// </summary>
    private readonly record struct Answer(JsonDocument? Document, bool Refused, bool Stale);

    /// <summary>
    /// One answer: the cache file while it is fresh and holds data, else the service (then cached, with what the app may
    /// not show cut), else the old file. An answer without data is a failed load. No document when there is neither.
    /// Never throws.
    /// </summary>
    private async Task<Answer> PartAsync(BgSitePart part, CancellationToken cancellationToken)
    {
        var file = CacheFile(part);
        var refused = false;
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && Clock() - info.LastWriteTimeUtc <= MaxAge && await ReadCacheAsync(part, file).ConfigureAwait(false) is { } fresh)
            {
                return new Answer(fresh, Refused: false, Stale: false);
            }

            if (await FetchAsync(part, cancellationToken).ConfigureAwait(false) is { } body && BgSiteParser.Strip(part, body) is { } kept)
            {
                var answer = JsonDocument.Parse(kept);
                if (BgSiteParser.HasData(part.Kind, answer.RootElement))
                {
                    await HearthpulseData.WriteCacheAsync(file, Encoding.UTF8.GetString(kept), cancellationToken).ConfigureAwait(false);
                    return new Answer(answer, Refused: false, Stale: false);
                }

                refused = part.Kind == BgSitePartKind.Heroes && BgSiteParser.HeroesSource(answer.RootElement) is null;
                answer.Dispose();
                Diag.Info($"Battlegrounds site {part.Name}: the answer has no data, not kept");
            }
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: what the cache has is still an answer.
        }
        catch (Exception ex)
        {
            // Whatever a handler or a stream throws is a failed load, like a network error: the old file answers.
            Diag.Error($"Battlegrounds site {part.Name}: not loaded", ex);
        }

        return new Answer(await ReadCacheAsync(part, file).ConfigureAwait(false), refused, Stale: true);
    }

    private string CacheFile(BgSitePart part) => Path.Combine(_cacheDirectory, $"bgsite-{part.Name}.json");

    /// <summary>The cache file when it is readable and holds data, else null. Never throws.</summary>
    private static async Task<JsonDocument?> ReadCacheAsync(BgSitePart part, string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var cached = JsonDocument.Parse(await File.ReadAllBytesAsync(file).ConfigureAwait(false));
            if (BgSiteParser.HasData(part.Kind, cached.RootElement))
            {
                return cached;
            }

            cached.Dispose();
            Diag.Info($"Battlegrounds site cache without data ({Path.GetFileName(file)})");
            return null;
        }
        catch (Exception ex)
        {
            Diag.Error($"Battlegrounds site cache unreadable ({Path.GetFileName(file)})", ex);
            return null;
        }
    }

    /// <summary>
    /// The answer's bytes, or null on a failed, slow, oversized, redirected or non-200 answer (logged). Throws only
    /// when the caller cancelled.
    /// </summary>
    private async Task<byte[]?> FetchAsync(BgSitePart part, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(Timeout, Time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, part.Path));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // A client that follows redirects reports where the answer came from: only the service's own host counts.
            if (response.RequestMessage?.RequestUri is { } answered
                && (answered.Scheme != Uri.UriSchemeHttps || !string.Equals(answered.Host, _base.Host, StringComparison.OrdinalIgnoreCase) || answered.Port != _base.Port))
            {
                Diag.Info($"Battlegrounds site {part.Name}: answered from another host, ignored");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Battlegrounds site {part.Name}: HTTP {(int)response.StatusCode}");
                return null;
            }

            var length = response.Content.Headers.ContentLength;
            if (length > MaxBytes)
            {
                Diag.Info($"Battlegrounds site {part.Name}: answer of {length} bytes ignored");
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream(length is > 0 ? (int)length.Value : 256 * 1024);
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    Diag.Info($"Battlegrounds site {part.Name}: answer over {MaxBytes / (1024 * 1024)} MB ignored");
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Diag.Info($"Battlegrounds site {part.Name}: no answer in {Timeout.TotalSeconds:0} s");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken compressed body (InvalidDataException), a disposed client and the like fail the load as a network error does.
            Diag.Info($"Battlegrounds site {part.Name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The service's address: only an absolute <c>https://</c> one replaces the default.</summary>
    private static Uri BaseAddress(string? baseUrl) =>
        baseUrl is not null && Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : new Uri(DefaultBaseUrl);
}
