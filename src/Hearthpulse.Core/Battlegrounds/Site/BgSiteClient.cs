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
/// window's tabs. Only <c>GET</c>, no sign-in and nothing about the player in a request; five requests a refresh.
/// Answers are cached on disk for <see cref="MaxAge"/>; an old file stands in when the server fails or answers
/// without data. Only data whose source is on the allow list is read or kept.
/// </summary>
/// <param name="http">A client that sends no cookies and follows no redirects: <see cref="CreateHttp"/>.</param>
/// <param name="baseUrl">The service's address; <see cref="DefaultBaseUrl"/> unless it is another <c>https://</c> one.</param>
/// <param name="cacheDirectory">Where answers are cached; the app's API cache folder by default (tests pass a scratch one).</param>
public sealed class BgSiteClient(HttpClient http, string? baseUrl = null, string? cacheDirectory = null)
{
    public const string DefaultBaseUrl = "https://bg.hs-manacost.ru/";

    /// <summary>The MMR slice the refresh reads for Duos heroes.</summary>
    public const string DefaultMmr = "TOP_50_PERCENT";

    /// <summary>How long an answer is kept before the service is asked again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>
    /// The MMR slices the service takes for heroes (<see cref="LoadHeroesAsync"/>) and trinkets
    /// (<see cref="LoadTrinketsAsync"/>), widest first.
    /// </summary>
    public static IReadOnlyList<string> MmrSlices { get; } = ["TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT"];

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
    /// Never throws. Null when no part could be had, from the service or the cache. Parts load independently: a part
    /// that failed is null or empty in the result, the others are there. Runs off the caller's thread.
    /// </summary>
    public Task<BgSiteStats?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCoreAsync(cancellationToken), CancellationToken.None);

    /// <summary>
    /// The heroes list of one mode and MMR slice (one of <see cref="MmrSlices"/>), cached like the rest. Null when the
    /// slice is unknown, could not be had, or its answer names no allowed source. Never throws.
    /// </summary>
    public Task<BgSiteTierList?> LoadHeroesAsync(bool duos, string mmr, CancellationToken cancellationToken = default) =>
        HeroSlice(duos, mmr) is { } part
            ? Task.Run(() => ListAsync(part, root => BgSiteParser.ParseHeroSlice(root, part.Name), cancellationToken), CancellationToken.None)
            : Task.FromResult<BgSiteTierList?>(null);

    /// <summary>
    /// The trinket tier list of one MMR slice (one of <see cref="MmrSlices"/>; the last 7 days), cached like the rest.
    /// Null when the slice is unknown, could not be had, or its source is not allowed. Never throws.
    /// </summary>
    public Task<BgSiteTierList?> LoadTrinketsAsync(string mmr, CancellationToken cancellationToken = default) =>
        MmrSlices.Contains(mmr, StringComparer.Ordinal)
            ? Task.Run(() => ListAsync(
                new BgSitePart($"trinkets-{mmr.ToLowerInvariant()}", $"api/bg/tier-lists?list=trinkets&mmr={mmr}&timeRange=LAST_7_DAYS", BgSitePartKind.TrinketList),
                root => BgSiteParser.ParseList(root, BgSiteList.Trinkets, heroes: null, "trinkets"), cancellationToken), CancellationToken.None)
            : Task.FromResult<BgSiteTierList?>(null);

    private static BgSitePart? HeroSlice(bool duos, string mmr)
    {
        var mode = duos ? "duos" : "solo";
        return MmrSlices.Contains(mmr, StringComparer.Ordinal)
            ? new BgSitePart($"heroes-{mode}-{mmr.ToLowerInvariant()}", $"api/bg/heroes?mode={mode}&mmr={mmr}", BgSitePartKind.HeroSlice)
            : null;
    }

    private async Task<BgSiteTierList?> ListAsync(BgSitePart part, Func<JsonElement, BgSiteTierList?> parse, CancellationToken cancellationToken)
    {
        using var answer = await PartAsync(part, cancellationToken).ConfigureAwait(false);
        return answer is null ? null : parse(answer.RootElement);
    }

    private async Task<BgSiteStats?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        var duos = HeroSlice(duos: true, DefaultMmr)!;
        BgSitePart[] parts = [TierLists, Heroes, duos, MinionStats, Comps];
        var answers = await Task.WhenAll(parts.Select(p => PartAsync(p, cancellationToken))).ConfigureAwait(false);
        try
        {
            return Array.TrueForAll(answers, a => a is null)
                ? null
                : Build(Root(0), Root(1), Root(2), Root(3), Root(4));
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
    internal static BgSiteStats Build(JsonElement tierLists, JsonElement heroes, JsonElement heroesDuos, JsonElement minionStats, JsonElement comps)
    {
        var details = BgSiteParser.ParseHeroes(heroes);
        var heroList = BgSiteParser.ParseTierList(tierLists, BgSiteList.Heroes, details);
        var duosList = heroesDuos.ValueKind == JsonValueKind.Undefined ? null : BgSiteParser.ParseHeroSlice(heroesDuos, "heroes-duos");
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
    /// One answer: the cache file while it is fresh and holds data, else the service (then cached, with what the app may
    /// not show cut), else the old file. An answer without data is a failed load. Null when there is neither. Never throws.
    /// </summary>
    private async Task<JsonDocument?> PartAsync(BgSitePart part, CancellationToken cancellationToken)
    {
        var file = Path.Combine(_cacheDirectory, $"bgsite-{part.Name}.json");
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && Clock() - info.LastWriteTimeUtc <= MaxAge && await ReadCacheAsync(part, file).ConfigureAwait(false) is { } fresh)
            {
                return fresh;
            }

            if (await FetchAsync(part, cancellationToken).ConfigureAwait(false) is { } body && BgSiteParser.Strip(part, body) is { } kept)
            {
                var answer = JsonDocument.Parse(kept);
                if (BgSiteParser.HasData(part.Kind, answer.RootElement))
                {
                    await HearthpulseData.WriteCacheAsync(file, Encoding.UTF8.GetString(kept), cancellationToken).ConfigureAwait(false);
                    return answer;
                }

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

        return await ReadCacheAsync(part, file).ConfigureAwait(false);
    }

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
