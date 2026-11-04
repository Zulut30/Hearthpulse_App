using System.Text.Json;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Api;

/// <summary>This week's Tavern Brawl from HSGuru (docs/brawl.md).</summary>
public sealed partial class HearthpulseData
{
    /// <summary>HSGuru's Brawl archetypes over all ranks: <c>total_games</c>, <c>items[]</c> {archetype, winrate, popularity, games}.</summary>
    internal const string BrawlMetaPath = "v1/hsguru/brawl/meta";

    private const string BrawlCacheName = "hsguru-brawl-meta.json";

    /// <summary>The Brawl by class; <see cref="BrawlMeta.Empty"/> when the server and the cache have nothing. Never throws.</summary>
    public async Task<BrawlMeta> LoadBrawlMetaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await FetchAsync(BrawlMetaPath, BrawlCacheName, cancellationToken).ConfigureAwait(false);
            var meta = doc is null ? BrawlMeta.Empty : Read(() => ParseBrawlMeta(Data(doc)), "brawl meta", BrawlMeta.Empty);
            // The cache file is written on each fetch: its time is when the numbers came.
            var file = new FileInfo(Path.Combine(_cacheDirectory, BrawlCacheName));
            return ReferenceEquals(meta, BrawlMeta.Empty) || !file.Exists ? meta : meta with { FetchedAt = file.LastWriteTimeUtc };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BrawlMeta.Empty;
        }
    }

    /// <summary>Parses <c>v1/hsguru/brawl/meta</c>'s <c>data</c>; rates arrive in percent.</summary>
    internal static BrawlMeta ParseBrawlMeta(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return BrawlMeta.Empty;
        }

        var archetypes = Items(data.TryGetProperty("items", out var items) ? items : default)
            // An archetype without a name or win rate still counts its games (as games no class could be told for).
            .Select(i => Num(i, "games") is { } games
                ? new BrawlArchetype(Str(i, "archetype") ?? string.Empty, Num(i, "winrate"), Num(i, "popularity") ?? 0, (int)games)
                : null)
            .OfType<BrawlArchetype>()
            .ToList();
        return BrawlStats.ByClass(archetypes, (int)(Num(data, "total_games") ?? 0));
    }
}
