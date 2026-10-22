using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

/// <summary>One shared Battlegrounds game, exactly what the pool server stores. Holds no player identity.</summary>
public sealed record PoolGame(string GameId, string Mode, int Bracket, IReadOnlyList<string> Offered, string Picked, int Placement)
{
    /// <summary>The record for a finished game, or null when it is not a Battlegrounds game with a placement.</summary>
    public static PoolGame? From(GameOutcome outcome, Func<string, string> baseHero)
    {
        var mode = outcome.Mode switch
        {
            "Battlegrounds" => "solo",
            "Duos" => "duos",
            _ => null,
        };
        if (mode is null || outcome.Placement is < 1 or > 8 || string.IsNullOrEmpty(outcome.HeroCardId))
        {
            return null;
        }

        var picked = baseHero(outcome.HeroCardId);
        var offered = (outcome.OfferedHeroes ?? []).Select(baseHero).Distinct(StringComparer.Ordinal).Take(4).ToList();

        // An offer list without the hero played would be rejected; the placement alone still counts.
        if (!offered.Contains(picked, StringComparer.Ordinal))
        {
            offered = [];
        }

        var bracket = outcome.RatingBefore is > 0 and var rating ? Math.Min(20000, rating / 500 * 500) : 0;
        return new PoolGame(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), mode, bracket, offered, picked, outcome.Placement);
    }
}

/// <summary>
/// Talks to the Hearthpulse stats pool (server/pool): games players chose to share, and the totals per hero.
/// </summary>
public static class PoolClient
{
    /// <summary>Address of the deployed pool (see server/pool/README.md). Empty keeps the pool off.</summary>
    public const string DefaultEndpoint = "";

    /// <summary>Heroes need this many shared games before their numbers are shown.</summary>
    public const int MinGames = 20;

    public static string Endpoint(AppSettings settings) =>
        (settings.PoolEndpoint is { Length: > 0 } custom ? custom : DefaultEndpoint).TrimEnd('/');

    public static bool Available(AppSettings settings) => Endpoint(settings).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> SubmitAsync(HttpClient http, string endpoint, PoolGame game, CancellationToken cancellationToken = default)
    {
        try
        {
            var body = new
            {
                v = 1,
                gameId = game.GameId,
                mode = game.Mode,
                bracket = game.Bracket,
                offered = game.Offered,
                picked = game.Picked,
                placement = game.Placement,
            };
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/games", body, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Pool refused the game: {(int)response.StatusCode}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Error("Sharing the game failed", ex);
            return false;
        }
    }

    /// <summary>Hero totals from the pool, in the shape the hero tier list and pick tiles already read.</summary>
    public static async Task<BattlegroundsMeta?> LoadHeroesAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http.GetStreamAsync(endpoint + "/v1/heroes?mode=solo&days=30", cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Diag.Error("Pool stats unavailable", ex);
            return null;
        }
    }

    internal static BattlegroundsMeta? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("heroes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var heroes = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        foreach (var hero in list.EnumerateArray())
        {
            var id = hero.ValueKind == JsonValueKind.Object && hero.TryGetProperty("hero", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            var games = Int(hero, "games");
            if (string.IsNullOrEmpty(id) || games < MinGames)
            {
                continue;
            }

            var offered = Int(hero, "offered");
            heroes[id] = new MetaHero(
                id,
                games,
                Number(hero, "avgPlacement"),
                offered > 0 ? (double)Int(hero, "picked") / offered : 0,
                new Dictionary<int, double>(),
                Number(hero, "top"),
                Number(hero, "first"));
        }

        var updated = root.TryGetProperty("updated", out var date) && date.ValueKind == JsonValueKind.String
            && DateTime.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
        return new BattlegroundsMeta(
            new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = heroes },
            [],
            [],
            new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>> { [100] = new Dictionary<string, MetaCard>() },
            updated)
        {
            Source = BattlegroundsMeta.PoolAttribution,
        };
    }

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}