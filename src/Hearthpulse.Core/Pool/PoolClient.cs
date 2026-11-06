using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

/// <summary>
/// Talks to the Hearthpulse stats pool (server/pool): games players chose to share, and the totals built from them.
/// </summary>
public static partial class PoolClient
{
    /// <summary>Address of the deployed pool (see server/pool/README.md). Empty keeps the pool off.</summary>
    public const string DefaultEndpoint = "";

    /// <summary>
    /// Raised whenever the list of shared fields grows. Players who agreed to an older list are asked again;
    /// until they answer, only the fields they agreed to are sent.
    /// </summary>
    public const int ConsentVersion = 4;

    /// <summary>Heroes and tribes need this many shared games before their numbers are shown.</summary>
    public const int MinGames = 20;

    /// <summary>Rated games the pool needs before its MMR floors count (they pick the bracket of the API's stats).</summary>
    public const int MinRatedGames = 1000;

    /// <summary>Games at or above an MMR floor of the pool before that floor (and its bracket) counts.</summary>
    public const int MinBracketGames = 50;

    /// <summary>Games of a hero with a tribe in the lobby before that tribe's impact counts.</summary>
    public const int TribeMinGames = 100;

    /// <summary>
    /// Shrinks a tribe impact toward zero by sample size, <c>delta · n / (n + k)</c>: the advisor adds up the impacts of
    /// five lobby tribes, so noise on a small sample would reorder the heroes.
    /// </summary>
    public const int TribeShrink = 150;

    /// <summary>Games sent in one request (the server takes up to 20).</summary>
    public const int MaxBatch = 20;

    /// <summary>Pause between two batches of earlier games: the server takes 6 requests a minute from one address.</summary>
    public static readonly TimeSpan HistoryBatchDelay = TimeSpan.FromSeconds(12);

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

    /// <summary>
    /// The Discover-style picks of Ranked and Casual Constructed games were added in version 4. Game records of every
    /// mode (docs/game-stats.md) are another purpose with their own opt-out switch (<see cref="AppSettings.GameStatsOff"/>).
    /// </summary>
    public static bool SharingPicks(AppSettings settings) => Sharing(settings) && settings.PoolConsentVersion >= 4;

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

    internal static object Body(IEnumerable<PoolGame> games) => new { v = 3, games = games.Select(Row).ToList() };

    /// <summary>One game as the pool's <c>POST /v1/games</c> takes it (game records reuse the Battlegrounds part).</summary>
    internal static Dictionary<string, object?> Row(PoolGame game)
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

    internal static object ConstructedBody(IEnumerable<ConstructedPoolGame> games) => new
    {
        v = 1,
        games = games.Select(g => new
        {
            id = g.GameId,
            day = g.Day,
            format = g.Format,
            won = g.Won,
            src = "live",
            picks = g.Picks.Select(p => new object[] { p.Turn, p.Source, p.Offered, p.Picked }),
        }).ToList(),
    };

    /// <summary>Sends finished Constructed games' picks (<see cref="SharingPicks"/>); false when the pool did not take them.</summary>
    public static async Task<bool> SubmitConstructedAsync(HttpClient http, string endpoint, IReadOnlyList<ConstructedPoolGame> games, CancellationToken cancellationToken = default)
    {
        if (games.Count is 0 or > MaxBatch)
        {
            return games.Count == 0;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/constructed", ConstructedBody(games), cancellationToken).ConfigureAwait(false);
            Diag.Info(response.IsSuccessStatusCode
                ? $"Shared the picks of {games.Count} Constructed game(s) with the stats pool"
                : $"Pool refused the Constructed picks: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Error("Sharing Constructed picks failed", ex);
            return false;
        }
    }

    /// <summary>Hero, card, trinket and tribe totals from the pool, in the shape the tier lists and pick tiles read.</summary>
    public static async Task<BattlegroundsMeta?> LoadStatsAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var stream = await http.GetStreamAsync(endpoint + "/v1/stats?mode=solo", cancellationToken).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Diag.Error("Pool stats unavailable", ex);
            return null;
        }
    }

    /// <summary>
    /// The full Battlegrounds meta from the pool alone: its base stats (/v1/meta, when the server has any) blended
    /// with the games players chose to share (/v1/stats).
    /// </summary>
    public static async Task<BattlegroundsMeta?> LoadMetaAsync(HttpClient http, string endpoint, CancellationToken cancellationToken = default)
    {
        var baseTask = LoadBaseMetaAsync(http, endpoint, cancellationToken);
        var poolTask = LoadStatsAsync(http, endpoint, cancellationToken);
        await Task.WhenAll(baseTask, poolTask).ConfigureAwait(false);

        var baseMeta = await baseTask.ConfigureAwait(false);
        var pool = await poolTask.ConfigureAwait(false);
        return baseMeta is null ? pool : pool is null ? baseMeta : Merge(baseMeta, pool);
    }

    /// <summary>
    /// Lays the pool's meta (<see cref="LoadMetaAsync"/>: its base stats and the shared games) over
    /// <paramref name="downloaded"/>, the meta the app loaded from the Hearthpulse API: the API's brackets stay, hero
    /// and card numbers blend by sample size where the API has one, and what the API lacks (comps, guides, trinkets,
    /// tribes, turn results, MMR floors) comes from the pool. Without a downloaded meta, the pool's own meta is used.
    /// </summary>
    public static async Task<BattlegroundsMeta?> OverlayAsync(HttpClient http, string endpoint, BattlegroundsMeta? downloaded, CancellationToken cancellationToken = default)
    {
        var pool = await LoadMetaAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
        return downloaded is null ? pool : pool is null ? downloaded : Merge(downloaded, pool);
    }

    private static async Task<BattlegroundsMeta?> LoadBaseMetaAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await http.GetStreamAsync(endpoint + "/v1/meta", cancellationToken).ConfigureAwait(false);
            await using var streamScope = stream.ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return ParseMeta(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Diag.Error("Stats pool meta unavailable", ex);
            return null;
        }
    }
}