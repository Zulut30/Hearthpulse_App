using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

public sealed record PoolMinion(string Card, bool Golden, int Attack, int Health);

/// <summary>One fight: W, T or L, damage both ways, and the win chance the simulator gave before it started.</summary>
public sealed record PoolCombat(int Turn, string Result, int Taken, int Dealt, double? Win);

/// <summary>The rest of a shared game (consent version 2 and up): lobby, final board, trinkets, tier-ups, fights, picks and plays.</summary>
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
    /// <summary>Every pick offered (Discover, triple rewards, trinkets...) and what was taken.</summary>
    public IReadOnlyList<BgChoice> Choices { get; init; } = [];

    /// <summary>Cards played from hand.</summary>
    public IReadOnlyList<BgPlay> Plays { get; init; } = [];

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
                c.Odds?.Win))
            .ToList();
        var picked = d.Trinkets.Distinct(StringComparer.Ordinal).Take(4).ToList();
        return new PoolDetails(
            Math.Clamp(d.Turns, 0, 99),
            Math.Clamp(d.Tier, 1, 7),
            Math.Clamp(d.Triples, 0, 99),
            string.IsNullOrEmpty(d.Anomaly) ? null : d.Anomaly,
            CleanTribes(d.Tribes),
            races is null ? null : MainTribe(board, races),
            board,
            d.TrinketsOffered.Concat(picked).Distinct(StringComparer.Ordinal).Take(12).ToList(),
            picked,
            tierUps,
            combats)
        {
            Choices = d.Choices
                .Where(c => c.Offered.Count is > 0 and <= 8)
                .Take(120)
                .Select(c => c with { Picked = c.Picked.Where(c.Offered.Contains).Take(4).ToList() })
                .ToList(),
            Plays = d.Plays.Take(300).ToList(),
        };
    }

    /// <summary>What a game saved in the local history still holds: final board, tier, length and lobby tribes.</summary>
    internal static PoolDetails? FromHistory(GameRecord record, Func<string, IReadOnlyList<string>>? races)
    {
        if (record.TavernTier is < 1 or > 7)
        {
            return null;
        }

        var board = new List<PoolMinion>();
        if (record.FinalBoard is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var m in doc.RootElement.EnumerateArray().Take(7))
                {
                    if (m.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } card)
                    {
                        board.Add(new PoolMinion(
                            card,
                            m.TryGetProperty("gold", out var gold) && gold.ValueKind == JsonValueKind.True,
                            m.TryGetProperty("atk", out var atk) && atk.TryGetInt32(out var a) ? Math.Max(0, a) : 0,
                            m.TryGetProperty("hp", out var hp) && hp.TryGetInt32(out var h) ? Math.Max(0, h) : 0));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                board.Clear();
            }
        }

        return new PoolDetails(
            Math.Clamp(record.Turns, 0, 99),
            record.TavernTier,
            0,
            null,
            CleanTribes((record.Races ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            races is null ? null : MainTribe(board, races),
            board,
            [],
            [],
            [],
            []);
    }

    private static List<string> CleanTribes(IEnumerable<string> tribes) =>
        tribes.Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8).ToList();

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
    /// <summary>Only sent when the player agreed to the longer list (consent version 2 and up).</summary>
    public PoolDetails? Details { get; init; }

    /// <summary>Day the game was played, in days since 1970-01-01 (UTC).</summary>
    public int Day { get; init; } = Today;

    /// <summary>"live" when sent as the game ended, "history" for an earlier game sent once.</summary>
    public string Source { get; init; } = "live";

    internal static int Today => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    /// <summary>The record for a finished game, or null when it is not a Battlegrounds game with a placement.</summary>
    public static PoolGame? From(
        GameOutcome outcome,
        Func<string, string> baseHero,
        Func<string, IReadOnlyList<string>>? races = null,
        bool details = false,
        string? secret = null)
    {
        var game = Basic(outcome.Mode, outcome.Placement, outcome.HeroCardId, outcome.RatingBefore, outcome.OfferedHeroes, outcome.Key, baseHero, secret);
        return game is null ? null : game with { Details = details && outcome.Details is { } d ? PoolDetails.From(d, races) : null };
    }

    /// <summary>A game from the local history, sent once when the player agrees to share.</summary>
    public static PoolGame? FromHistory(GameRecord record, string secret, Func<string, string> baseHero, Func<string, IReadOnlyList<string>>? races = null)
    {
        var game = Basic(record.Mode, record.Placement, record.HeroCardId, record.RatingBefore, null, record.Key, baseHero, secret);
        return game is null ? null : game with
        {
            Details = PoolDetails.FromHistory(record, races),
            Day = (int)(record.StartedAt.ToUniversalTime() - DateTime.UnixEpoch).TotalDays,
            Source = "history",
        };
    }

    private static PoolGame? Basic(
        string modeName,
        int placement,
        string heroCardId,
        int? ratingBefore,
        IReadOnlyList<string>? offeredHeroes,
        string? key,
        Func<string, string> baseHero,
        string? secret)
    {
        var mode = modeName switch
        {
            "Battlegrounds" => "solo",
            "Duos" => "duos",
            _ => null,
        };
        if (mode is null || placement is < 1 or > 8 || string.IsNullOrEmpty(heroCardId))
        {
            return null;
        }

        var picked = baseHero(heroCardId);
        var offered = (offeredHeroes ?? []).Select(baseHero).Distinct(StringComparer.Ordinal).Take(4).ToList();

        // An offer list without the hero played would be rejected; the placement alone still counts.
        if (!offered.Contains(picked, StringComparer.Ordinal))
        {
            offered = [];
        }

        var bracket = ratingBefore is > 0 and var rating ? Math.Min(20000, rating / 500 * 500) : 0;
        return new PoolGame(Id(key, secret), mode, bracket, offered, picked, placement);
    }

    /// <summary>
    /// The same game always gets the same id from this install, so sending it twice stores it once. The id is a keyed
    /// hash with a secret that never leaves this PC: it cannot be turned back into the game, the player or the account.
    /// </summary>
    internal static string Id(string? key, string? secret) =>
        key is { Length: > 0 } && secret is { Length: > 0 }
            ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)).ToLowerInvariant()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
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
    public const int ConsentVersion = 3;

    /// <summary>Heroes and tribes need this many shared games before their numbers are shown.</summary>
    public const int MinGames = 20;

    /// <summary>Games sent in one request (the server takes up to 20).</summary>
    public const int MaxBatch = 20;

    /// <summary>How far back earlier games are shared once, matching the pool's 30 day window.</summary>
    public const int HistoryDays = 30;

    public static string Endpoint(AppSettings settings) =>
        (settings.PoolEndpoint is { Length: > 0 } custom ? custom : DefaultEndpoint).TrimEnd('/');

    public static bool Available(AppSettings settings) => Endpoint(settings).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The player agreed to share, and to which version of the list.</summary>
    public static bool Sharing(AppSettings settings) => settings.PoolConsent == true && Available(settings);

    public static bool SharingDetails(AppSettings settings) => Sharing(settings) && settings.PoolConsentVersion >= 2;

    /// <summary>Picks, plays and earlier games were added in version 3.</summary>
    public static bool SharingAll(AppSettings settings) => Sharing(settings) && settings.PoolConsentVersion >= 3;

    /// <summary>The install's secret for game ids, made on first use. Callers save the settings afterwards.</summary>
    public static string Secret(AppSettings settings)
    {
        if (settings.PoolSecret is not { Length: 64 })
        {
            settings.PoolSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        return settings.PoolSecret;
    }

    /// <summary>Removes what version 2 consent did not cover (picks and plays) from a game.</summary>
    public static PoolGame Limit(PoolGame game, AppSettings settings) =>
        game.Details is { } d && !SharingAll(settings) ? game with { Details = d with { Choices = [], Plays = [] } } : game;

    internal static object Body(IEnumerable<PoolGame> games) => new { v = 3, games = games.Select(Game).ToList() };

    private static Dictionary<string, object?> Game(PoolGame game)
    {
        var row = new Dictionary<string, object?>
        {
            ["id"] = game.GameId,
            ["day"] = game.Day,
            ["mode"] = game.Mode,
            ["bracket"] = game.Bracket,
            ["offered"] = game.Offered,
            ["picked"] = game.Picked,
            ["placement"] = game.Placement,
            ["src"] = game.Source,
        };
        if (game.Details is not { } d)
        {
            return row;
        }

        row["turns"] = d.Turns;
        row["tier"] = d.Tier;
        row["triples"] = d.Triples;
        row["anomaly"] = d.Anomaly;
        row["tribes"] = d.Tribes;
        row["comp"] = d.Comp;
        row["board"] = d.Board.Select(m => new { c = m.Card, g = m.Golden, a = m.Attack, h = m.Health });
        row["trinkets"] = new { o = d.TrinketsOffered, p = d.Trinkets };
        row["tierups"] = d.TierUps.Select(t => new[] { t.Turn, t.Tier });
        row["combats"] = d.Combats.Select(c => new object?[] { c.Turn, c.Result, c.Taken, c.Dealt, c.Win is { } w ? Math.Round(w, 3) : null });
        row["choices"] = d.Choices.Select(c => new object?[] { c.Turn, c.Kind, c.Source, c.Offered, c.Picked });
        row["plays"] = d.Plays.Select(p => new object[] { p.Turn, p.CardId });
        return row;
    }

    public static async Task<bool> SubmitAsync(HttpClient http, string endpoint, IReadOnlyList<PoolGame> games, CancellationToken cancellationToken = default)
    {
        if (games.Count is 0 or > MaxBatch)
        {
            return games.Count == 0;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/games", Body(games), cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Diag.Info($"Shared {games.Count} game(s) with the stats pool");
            }
            else
            {
                Diag.Info($"Pool refused the games: {(int)response.StatusCode}");
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Error("Sharing games failed", ex);
            return false;
        }
    }

    /// <summary>Hero, card, trinket and tribe totals from the pool, in the shape the tier lists and pick tiles read.</summary>
    public static async Task<BattlegroundsMeta?> LoadStatsAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http.GetStreamAsync(endpoint + "/v1/stats?mode=solo", cancellationToken).ConfigureAwait(false);
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