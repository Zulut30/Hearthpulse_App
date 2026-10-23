using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Constructed;

namespace Hearthpulse.Core.Api;

/// <summary>
/// Community stats from the Hearthpulse API (HSReplay-based): Battlegrounds heroes and minions, ranked Constructed
/// archetypes. The app's main source stays Firestone; these fill in when it cannot be reached. Answers are cached on
/// disk, and a stale cache is used when the server is down. Never throws.
/// </summary>
public sealed class HearthpulseData(HearthpulseApi api)
{
    public const string Attribution = "Community stats: Hearthpulse API (HSReplay)";

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>Solo (or "duos") hero results, by hero card id. Sample sizes are not published: DataPoints is 0.</summary>
    public async Task<IReadOnlyDictionary<string, MetaHero>> LoadHeroesAsync(string mode = "solo", CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"v1/battlegrounds/heroes?mode={mode}&limit=200", $"bg-heroes-{mode}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? new Dictionary<string, MetaHero>() : Read(() => ParseHeroes(Data(doc)), "heroes", new Dictionary<string, MetaHero>());
    }

    /// <summary>All-ranks minion results, by card id: average final placement of players who had the minion.</summary>
    public async Task<IReadOnlyDictionary<string, MetaCard>> LoadMinionsAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync("v1/battlegrounds/minions/stats?limit=500", "bg-minions.json", cancellationToken).ConfigureAwait(false);
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

    /// <summary>HSReplay publishes Legend and a few brackets; anything wider than Legend maps to its all-ranks view.</summary>
    internal static string RankRange(string rank) => rank switch
    {
        "legend" or "top-2000-legend" or "top-500-legend" or "top-100-legend" => "LEGEND",
        "diamond" => "DIAMOND_THROUGH_LEGEND",
        _ => "ALL",
    };

    internal static Dictionary<string, MetaHero> ParseHeroes(JsonElement list)
    {
        var result = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        foreach (var hero in Items(list))
        {
            if (Str(hero, "id") is not { Length: > 0 } id || Num(hero, "avg_placement") is not { } placement || placement <= 0)
            {
                continue;
            }

            double top4 = 0, first = 0;
            if (hero.TryGetProperty("placement_distribution", out var places) && places.ValueKind == JsonValueKind.Array)
            {
                var rank = 0;
                foreach (var place in places.EnumerateArray())
                {
                    rank++;
                    var share = Percent(place) / 100;
                    top4 += rank <= 4 ? share : 0;
                    first = rank == 1 ? share : first;
                }
            }

            result[id] = new MetaHero(id, 0, placement, (Num(hero, "pick_rate_value") ?? 0) / 100, new Dictionary<int, double>(), Math.Min(1, top4), first);
        }

        return result;
    }

    internal static Dictionary<string, MetaCard> ParseMinions(JsonElement list)
    {
        var result = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        foreach (var minion in Items(list))
        {
            if (Str(minion, "card_id") is not { Length: > 0 } id || !minion.TryGetProperty("normal", out var normal) || normal.ValueKind != JsonValueKind.Object
                || Num(normal, "avg_placement_with") is not { } placement || placement <= 0)
            {
                continue;
            }

            result[id] = new MetaCard(id, (int)(Num(normal, "games_with_minion") ?? 0), placement, new Dictionary<int, (int, double)>());
        }

        return result;
    }

    internal static List<MetaArchetype> ParseArchetypes(JsonElement root)
    {
        var result = new List<MetaArchetype>();
        if (!root.TryGetProperty("archetypes", out var list))
        {
            return result;
        }

        foreach (var a in Items(list))
        {
            if (Num(a, "archetype_id") is not { } id || Str(a, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var games = (int)(Num(a, "total_games") ?? 0);
            var winRate = (Num(a, "win_rate") ?? 0) / 100;
            result.Add(new MetaArchetype(
                (int)id,
                Str(a, "slug") ?? name,
                name,
                Str(a, "player_class") ?? string.Empty,
                games,
                (int)Math.Round(games * winRate),
                winRate,
                (Num(a, "pct_of_total") ?? 0) / 100,
                0, 0, 0, 0, [], []));
        }

        return result;
    }

    private async Task<JsonDocument?> FetchAsync(string path, string cacheName, CancellationToken cancellationToken)
    {
        var file = Path.Combine(AppPaths.Cache, "api", cacheName);
        var info = new FileInfo(file);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            var result = await api.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    // The whole answer is kept (not only "data"): some endpoints put their list next to it.
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { data = result.Data }), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Diag.Error($"Hearthpulse API cache not written ({cacheName})", ex);
                }

                return JsonDocument.Parse(JsonSerializer.Serialize(new { data = result.Data }));
            }

            Diag.Info($"Hearthpulse API {path}: {result.Error!.Code} {result.Error.Message}");
        }

        try
        {
            return File.Exists(file) ? JsonDocument.Parse(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Diag.Error($"Hearthpulse API cache unreadable ({cacheName})", ex);
            return null;
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

    private static IEnumerable<JsonElement> Items(JsonElement list) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : [];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>"22.00%" or 22.0.</summary>
    private static double Percent(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String when double.TryParse(e.GetString()!.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
        _ => 0,
    };
}
