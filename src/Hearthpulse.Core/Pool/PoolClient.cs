using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

public sealed record PoolMinion(string Card, bool Golden, int Attack, int Health);

/// <summary>One fight: W, T or L, damage both ways, and the odds the simulator gave before it started.</summary>
public sealed record PoolCombat(int Turn, string Result, int Taken, int Dealt, double? Win, double? Tie, double? Loss);

/// <summary>The rest of a shared game (consent version 2): the lobby, your final board, trinkets, tier-ups and fights.</summary>
public sealed record PoolDetails(
    int Turns,
    int Tier,
    int Triples,
    string? Anomaly,
    IReadOnlyList<string> Tribes,
    string? Comp,
    IReadOnlyList<PoolMinion> Board,
    IReadOnlyList<string> TrinketsOffered,
    IReadOnlyList<string> Trinkets,
    IReadOnlyList<TierStep> TierUps,
    IReadOnlyList<PoolCombat> Combats)
{
    internal static PoolDetails From(BgGameDetails d, Func<string, IReadOnlyList<string>>? races)
    {
        var board = d.Board
            .Where(m => m.CardType == "MINION" && m.CardId.Length > 0)
            .Take(7)
            .Select(m => new PoolMinion(m.CardId, m.Golden, Math.Max(0, m.Attack), Math.Max(0, m.Health)))
            .ToList();
        var tierUps = d.TierUps
            .Where(t => t.Tier is >= 2 and <= 7)
            .GroupBy(t => t.Tier)
            .Select(g => g.First())
            .OrderBy(t => t.Tier)
            .ToList();
        var combats = d.Combats
            .TakeLast(60)
            .Select(c => new PoolCombat(
                c.Turn,
                c.Result switch { "Win" => "W", "Loss" => "L", _ => "T" },
                Math.Clamp(c.DamageTaken, 0, 200),
                Math.Clamp(c.DamageDealt, 0, 200),
                c.Odds?.Win,
                c.Odds?.Tie,
                c.Odds?.Loss))
            .ToList();
        var picked = d.Trinkets.Distinct(StringComparer.Ordinal).Take(4).ToList();
        return new PoolDetails(
            Math.Clamp(d.Turns, 0, 99),
            Math.Clamp(d.Tier, 1, 7),
            Math.Clamp(d.Triples, 0, 99),
            string.IsNullOrEmpty(d.Anomaly) ? null : d.Anomaly,
            d.Tribes.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToList(),
            races is null ? null : MainTribe(board, races),
            board,
            d.TrinketsOffered.Concat(picked).Distinct(StringComparer.Ordinal).Take(8).ToList(),
            picked,
            tierUps,
            combats);
    }

    /// <summary>The tribe of at least 4 minions on the final board (amalgams count for every tribe), or MIXED.</summary>
    internal static string? MainTribe(IReadOnlyList<PoolMinion> board, Func<string, IReadOnlyList<string>> races)
    {
        if (board.Count == 0)
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var all = 0;
        foreach (var minion in board)
        {
            var list = races(minion.Card);
            if (list.Contains("ALL"))
            {
                all++;
                continue;
            }

            foreach (var race in list.Where(r => r is { Length: > 0 } and not "INVALID").Distinct())
            {
                counts[race] = counts.GetValueOrDefault(race) + 1;
            }
        }

        var best = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).FirstOrDefault();
        return best.Key is not null && best.Value + all >= 4 ? best.Key : "MIXED";
    }
}

/// <summary>One shared Battlegrounds game, exactly what the pool server stores. Holds no player identity.</summary>
public sealed record PoolGame(string GameId, string Mode, int Bracket, IReadOnlyList<string> Offered, string Picked, int Placement)
{
    /// <summary>Only sent when the player agreed to the longer list (consent version 2).</summary>
    public PoolDetails? Details { get; init; }

    /// <summary>The record for a finished game, or null when it is not a Battlegrounds game with a placement.</summary>
    public static PoolGame? From(
        GameOutcome outcome,
        Func<string, string> baseHero,
        Func<string, IReadOnlyList<string>>? races = null,
        bool details = false)
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
        return new PoolGame(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), mode, bracket, offered, picked, outcome.Placement)
        {
            Details = details && outcome.Details is { } d ? PoolDetails.From(d, races) : null,
        };
    }
}

/// <summary>Totals for the main tribe of final boards.</summary>
public sealed record PoolTribe(string Tribe, int Games, double AveragePlacement, double Top4Rate, double FirstRate);

/// <summary>
/// Talks to the Hearthpulse stats pool (server/pool): games players chose to share, and the totals built from them.
/// </summary>
public static class PoolClient
{
    /// <summary>Address of the deployed pool (see server/pool/README.md). Empty keeps the pool off.</summary>
    public const string DefaultEndpoint = "";

    /// <summary>
    /// Raised whenever the list of shared fields grows. Players who agreed to an older list are asked again;
    /// until they answer, only the fields they agreed to are sent.
    /// </summary>
    public const int ConsentVersion = 2;

    /// <summary>Heroes and tribes need this many shared games before their numbers are shown.</summary>
    public const int MinGames = 20;

    public static string Endpoint(AppSettings settings) =>
        (settings.PoolEndpoint is { Length: > 0 } custom ? custom : DefaultEndpoint).TrimEnd('/');

    public static bool Available(AppSettings settings) => Endpoint(settings).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The player agreed to share, and to which version of the list.</summary>
    public static bool Sharing(AppSettings settings) => settings.PoolConsent == true && Available(settings);

    public static bool SharingDetails(AppSettings settings) => Sharing(settings) && settings.PoolConsentVersion >= ConsentVersion;

    internal static object Body(PoolGame game) => game.Details is not { } d
        ? new { v = 1, gameId = game.GameId, mode = game.Mode, bracket = game.Bracket, offered = game.Offered, picked = game.Picked, placement = game.Placement }
        : new
        {
            v = 2,
            gameId = game.GameId,
            mode = game.Mode,
            bracket = game.Bracket,
            offered = game.Offered,
            picked = game.Picked,
            placement = game.Placement,
            details = new
            {
                turns = d.Turns,
                tier = d.Tier,
                triples = d.Triples,
                anomaly = d.Anomaly,
                tribes = d.Tribes,
                comp = d.Comp,
                board = d.Board.Select(m => new { card = m.Card, golden = m.Golden, atk = m.Attack, hp = m.Health }),
                trinkets = new { offered = d.TrinketsOffered, picked = d.Trinkets },
                tierUps = d.TierUps.Select(t => new[] { t.Turn, t.Tier }),
                combats = d.Combats.Select(c => new { turn = c.Turn, result = c.Result, taken = c.Taken, dealt = c.Dealt, win = c.Win, tie = c.Tie, loss = c.Loss }),
            },
        };

    public static async Task<bool> SubmitAsync(HttpClient http, string endpoint, PoolGame game, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/games", Body(game), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"Pool refused the game: {(int)response.StatusCode}");
            }
            else
            {
                Diag.Info($"Shared game with the stats pool ({(game.Details is null ? "basic" : "full")})");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Error("Sharing the game failed", ex);
            return false;
        }
    }

    /// <summary>Hero, card, trinket and tribe totals from the pool, in the shape the tier lists and pick tiles read.</summary>
    public static async Task<BattlegroundsMeta?> LoadStatsAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http.GetStreamAsync(endpoint + "/v1/stats?mode=solo&days=30", cancellationToken).ConfigureAwait(false);
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
            var id = Text(hero, "hero");
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

        var cards = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        foreach (var card in Array(root, "cards"))
        {
            if (Text(card, "card") is { Length: > 0 } id)
            {
                cards[id] = new MetaCard(id, Int(card, "games"), Number(card, "avgPlacement"), new Dictionary<int, (int, double)>());
            }
        }

        var trinkets = new Dictionary<string, MetaTrinket>(StringComparer.Ordinal);
        foreach (var trinket in Array(root, "trinkets"))
        {
            if (Text(trinket, "card") is { Length: > 0 } id)
            {
                var offered = Int(trinket, "offered");
                trinkets[id] = new MetaTrinket(
                    id,
                    Int(trinket, "picked"),
                    Number(trinket, "avgPlacement"),
                    offered > 0 ? (double)Int(trinket, "picked") / offered : 0,
                    new Dictionary<int, (double, int)>());
            }
        }

        var tribes = Array(root, "comps")
            .Select(c => new PoolTribe(Text(c, "comp") ?? string.Empty, Int(c, "games"), Number(c, "avgPlacement"), Number(c, "top"), Number(c, "first")))
            .Where(t => t.Tribe.Length > 0 && t.Games >= MinGames)
            .ToList();

        var updated = Text(root, "updated") is { } date
            && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
        return new BattlegroundsMeta(
            new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = heroes },
            [],
            [],
            trinkets,
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>> { [100] = cards },
            updated)
        {
            Tribes = tribes,
            Source = BattlegroundsMeta.PoolAttribution,
        };
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}