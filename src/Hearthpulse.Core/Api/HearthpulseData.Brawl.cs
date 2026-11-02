using System.Text.Json;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Api;

/// <summary>This week's Tavern Brawl from HSGuru (docs/brawl.md).</summary>
public sealed partial class HearthpulseData
{
    /// <summary>HSGuru's Brawl archetypes over all ranks: <c>total_games</c>, <c>items[]</c> {archetype, winrate, popularity, games}.</summary>
    internal const string BrawlMetaPath = "v1/hsguru/brawl/meta";

    /// <summary>The Brawl by class; <see cref="BrawlMeta.Empty"/> when the server and the cache have nothing. Never throws.</summary>
    public async Task<BrawlMeta> LoadBrawlMetaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await FetchAsync(BrawlMetaPath, "hsguru-brawl-meta.json", cancellationToken).ConfigureAwait(false);
            return doc is null ? BrawlMeta.Empty : Read(() => ParseBrawlMeta(Data(doc)), "brawl meta", BrawlMeta.Empty);
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
            .Select(i => Str(i, "archetype") is { Length: > 0 } name && Num(i, "winrate") is { } winRate && Num(i, "games") is { } games
                ? new BrawlArchetype(name, winRate, Num(i, "popularity") ?? 0, (int)games)
                : null)
            .OfType<BrawlArchetype>()
            .ToList();
        return BrawlStats.ByClass(archetypes, (int)(Num(data, "total_games") ?? 0));
    }
}
