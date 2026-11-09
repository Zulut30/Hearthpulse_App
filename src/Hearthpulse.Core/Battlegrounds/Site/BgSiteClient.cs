using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.Battlegrounds.Site;

/// <summary>
/// The Battlegrounds lists hearthpulse.net shows, read from the open service the site itself proxies
/// (docs/bg-site-stats.md): tier lists of heroes, minions and trinkets, minion numbers and comps, for the main
/// window's tabs. Only <c>GET</c>, no sign-in and nothing about the player in a request; four requests a refresh.
/// Answers are cached on disk for <see cref="MaxAge"/>, and an old file stands in when the server fails.
/// </summary>
/// <param name="baseUrl">The service's address; <see cref="DefaultBaseUrl"/> unless it is another <c>https://</c> one.</param>
/// <param name="cacheDirectory">Where answers are cached; the app's API cache folder by default (tests pass a scratch one).</param>
public sealed class BgSiteClient(HttpClient http, string? baseUrl = null, string? cacheDirectory = null)
{
    public const string DefaultBaseUrl = "https://bg.hs-manacost.ru/";

    /// <summary>How long an answer is kept before the service is asked again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>One request's time limit.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The largest answer is under 1 MB; anything past this is not one of the lists.</summary>
    internal const int MaxBytes = 8 * 1024 * 1024;

    internal const string TierListsPart = "tier-lists";
    internal const string HeroesPart = "heroes";
    internal const string MinionStatsPart = "minion-stats";
    internal const string CompsPart = "comps";

    /// <summary>The addresses read on a refresh, by cache part (the limit is 8 requests).</summary>
    internal static readonly IReadOnlyList<(string Part, string Path)> Parts =
    [
        (TierListsPart, "api/bg/tier-lists"),
        (HeroesPart, "api/bg/heroes"),
        (MinionStatsPart, "api/bg/library/minion-stats"),
        (CompsPart, "api/bg-comps"),
    ];

    private readonly Uri _base = BaseAddress(baseUrl);
    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");

    /// <summary>Now (UTC), for the age of the cache files; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Counts <see cref="Timeout"/>; tests pass a clock they move.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Never throws. Null when no part could be had, from the service or the cache. Parts load independently: a part
    /// that failed is null or empty in the result, the others are there.
    /// </summary>
    public async Task<BgSiteStats?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loads = Parts.Select(p => PartAsync(p.Part, p.Path, cancellationToken)).ToList();
        var answers = await Task.WhenAll(loads).ConfigureAwait(false);
        try
        {
            if (Array.TrueForAll(answers, a => a is null))
            {
                return null;
            }

            return Build(Root(TierListsPart), Root(HeroesPart), Root(MinionStatsPart), Root(CompsPart));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer?.Dispose();
            }
        }

        JsonElement Root(string part)
        {
            for (var i = 0; i < Parts.Count; i++)
            {
                if (Parts[i].Part == part)
                {
                    return answers[i]?.RootElement ?? default;
                }
            }

            return default;
        }
    }

    /// <summary>The records from the four answers; an answer that is missing has <see cref="JsonValueKind.Undefined"/>.</summary>
    internal static BgSiteStats Build(JsonElement tierLists, JsonElement heroes, JsonElement minionStats, JsonElement comps)
    {
        var details = BgSiteParser.ParseHeroes(heroes);
        var heroList = BgSiteParser.ParseTierList(tierLists, BgSiteList.Heroes, details);
        var minions = BgSiteParser.ParseTierList(tierLists, BgSiteList.Minions, heroes: null);
        var spells = BgSiteParser.ParseTierList(tierLists, BgSiteList.Spells, heroes: null);
        var trinkets = BgSiteParser.ParseTierList(tierLists, BgSiteList.Trinkets, heroes: null);
        var (stats, run) = BgSiteParser.ParseMinionStats(minionStats);
        var (compList, compsAt) = BgSiteParser.ParseComps(comps);

        var times = new List<DateTime?> { heroList?.FetchedAt, minions?.FetchedAt, spells?.FetchedAt, trinkets?.FetchedAt, run?.CompletedAt };
        if (compList.Count > 0)
        {
            times.Add(compsAt);
        }

        return new BgSiteStats(heroList, HeroesDuos: null, minions, spells, trinkets, stats,
            new Dictionary<string, BgSiteCardStat>(StringComparer.Ordinal), compList, run, times.Max() ?? default);
    }

    /// <summary>
    /// One answer: the cache file while it is fresh and readable, else the service (then cached, refused lists cut),
    /// else the old file. Null when there is neither.
    /// </summary>
    private async Task<JsonDocument?> PartAsync(string part, string path, CancellationToken cancellationToken)
    {
        var file = Path.Combine(_cacheDirectory, $"bgsite-{part}.json");
        try
        {
            var info = new FileInfo(file);
            if (info.Exists && Clock() - info.LastWriteTimeUtc <= MaxAge && ReadCache(file) is { } fresh)
            {
                return fresh;
            }

            if (await FetchAsync(part, path, cancellationToken).ConfigureAwait(false) is { } body && BgSiteParser.Strip(part, body) is { } text)
            {
                await HearthpulseData.WriteCacheAsync(file, text, cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(text);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller gave up: what the cache has is still an answer.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Error($"Battlegrounds site {part}: not loaded", ex);
        }

        return ReadCache(file);
    }

    private static JsonDocument? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonDocument.Parse(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Error($"Battlegrounds site cache unreadable ({Path.GetFileName(file)})", ex);
            return null;
        }
    }

    /// <summary>The answer's text, or null on a failed, slow, oversized or non-200 answer (logged).</summary>
    private async Task<string?> FetchAsync(string part, string path, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(Timeout, Time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Battlegrounds site {part}: HTTP {(int)response.StatusCode}");
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                Diag.Info($"Battlegrounds site {part}: answer of {response.Content.Headers.ContentLength} bytes ignored");
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    Diag.Info($"Battlegrounds site {part}: answer over {MaxBytes / (1024 * 1024)} MB ignored");
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('﻿');
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Diag.Info($"Battlegrounds site {part}: no answer in {Timeout.TotalSeconds:0} s");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Diag.Info($"Battlegrounds site {part}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The service's address: only an absolute <c>https://</c> one replaces the default.</summary>
    private static Uri BaseAddress(string? baseUrl) =>
        baseUrl is not null && Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : new Uri(DefaultBaseUrl);
}
