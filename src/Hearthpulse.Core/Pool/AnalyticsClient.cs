using System.Text.Json;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Pool;

/// <summary>A share with its Wilson interval; <see cref="Value"/>/<see cref="Low"/>/<see cref="High"/> are null for a small sample.</summary>
public sealed record AnalyticsRate(int Numerator, int Denominator, double? Value, double? Low, double? High);

/// <summary>Placements of the games where the hero was picked: count, mean with its interval, and places 1..8.</summary>
public sealed record AnalyticsPlacement(int Count, double? Mean, double? Low, double? High, IReadOnlyList<int> Distribution);

/// <summary>One hero of metric <c>bg.hero.offer_pick_placement</c>; <see cref="Quality"/> is "ok" or "small_sample".</summary>
public sealed record BgHeroAnalyticsRow(string HeroCardId, int Offered, int Picked, AnalyticsRate PickRate,
    AnalyticsPlacement Placement, AnalyticsRate Top4, AnalyticsRate First, string Quality);

/// <summary>One game build (null = unknown); <see cref="Suppressed"/> heroes were offered by too few sources to show.</summary>
public sealed record BgHeroAnalyticsGroup(int? Build, int Captures, int Sources, int Suppressed, IReadOnlyList<BgHeroAnalyticsRow> Heroes);

/// <summary>
/// The published snapshot. When <see cref="Available"/> is false, <see cref="Reason"/> says why
/// ("not_published", "rebuilding", "unsupported_mode") and there are no groups.
/// </summary>
public sealed record BgHeroAnalytics(bool Available, string? Reason, string Metric, string RulesVersion, string Mode,
    long Published, bool Stale, int FromDay, int ToDay, IReadOnlyList<BgHeroAnalyticsGroup> Groups);

/// <summary>
/// Reads the pool's analytics snapshots (docs/analytics-api.md). The endpoint is the pool base, as for
/// <see cref="PoolClient.Endpoint"/> (for example <c>https://api.kolodahearthstone.com/stats</c>).
/// </summary>
public static class AnalyticsClient
{
    private const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>null on network error, non-success status, invalid JSON or a non-https endpoint.</summary>
    public static async Task<BgHeroAnalytics?> LoadBgHeroesAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/v2/analytics/bg/heroes?mode=solo");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            using var doc = JsonDocument.Parse(buffer.ToArray());
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException)
        {
            Diag.Error("Pool analytics unavailable", ex);
            return null;
        }
    }

    /// <summary>The response body of contract C5; throws on a shape it does not recognise.</summary>
    internal static BgHeroAnalytics? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("v").GetInt32() != 2)
            return null;
        var available = root.GetProperty("available").GetBoolean();
        var period = root.TryGetProperty("period", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var groups = new List<BgHeroAnalyticsGroup>();
        if (available)
        {
            foreach (var group in root.GetProperty("groups").EnumerateArray())
            {
                var heroes = new List<BgHeroAnalyticsRow>();
                foreach (var hero in group.GetProperty("heroes").EnumerateArray())
                {
                    var placement = hero.GetProperty("placement");
                    heroes.Add(new BgHeroAnalyticsRow(
                        hero.GetProperty("hero").GetString() ?? throw new FormatException("hero"),
                        hero.GetProperty("offered").GetInt32(),
                        hero.GetProperty("picked").GetInt32(),
                        Rate(hero.GetProperty("pickRate")),
                        new AnalyticsPlacement(placement.GetProperty("n").GetInt32(), Number(placement, "mean"), Number(placement, "low"),
                            Number(placement, "high"), [.. placement.GetProperty("dist").EnumerateArray().Select(x => x.GetInt32())]),
                        Rate(hero.GetProperty("top4")),
                        Rate(hero.GetProperty("first")),
                        hero.GetProperty("quality").GetString() ?? throw new FormatException("quality")));
                }

                var build = group.GetProperty("build");
                groups.Add(new BgHeroAnalyticsGroup(build.ValueKind == JsonValueKind.Null ? null : build.GetInt32(),
                    group.GetProperty("captures").GetInt32(), group.GetProperty("sources").GetInt32(),
                    group.GetProperty("suppressed").GetInt32(), heroes));
            }
        }

        var reason = root.GetProperty("reason");
        var published = root.GetProperty("published");
        return new BgHeroAnalytics(
            available,
            reason.ValueKind == JsonValueKind.Null ? null : reason.GetString(),
            root.GetProperty("metric").GetString() ?? throw new FormatException("metric"),
            root.GetProperty("rulesVersion").GetString() ?? throw new FormatException("rulesVersion"),
            root.GetProperty("mode").GetString() ?? throw new FormatException("mode"),
            published.ValueKind == JsonValueKind.Null ? 0 : published.GetInt64(),
            root.GetProperty("stale").GetBoolean(),
            period.ValueKind == JsonValueKind.Object ? period.GetProperty("fromDay").GetInt32() : 0,
            period.ValueKind == JsonValueKind.Object ? period.GetProperty("toDay").GetInt32() : 0,
            groups);
    }

    private static AnalyticsRate Rate(JsonElement x) =>
        new(x.GetProperty("num").GetInt32(), x.GetProperty("den").GetInt32(), Number(x, "value"), Number(x, "low"), Number(x, "high"));

    private static double? Number(JsonElement x, string name)
    {
        var value = x.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    }
}
