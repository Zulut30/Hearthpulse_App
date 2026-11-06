using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Pool;

namespace Hearthpulse.Core.GameStats;

/// <summary>What the stats pool answered to a batch of game records.</summary>
public enum GameStatsAnswer
{
    /// <summary>Stored (or already there).</summary>
    Stored,

    /// <summary>Refused for good (a bad record, too big): the batch is dropped, never retried.</summary>
    Refused,

    /// <summary>Not now (offline, rate limit, the day's budget, a server error): the batch waits and is retried.</summary>
    Later,
}

/// <summary>
/// The stats pool's game record API (server/pool/src/events.js, docs/game-stats.md): <c>POST /v1/events</c> takes
/// 1-20 records of one install, <c>POST /v1/events/forget</c> deletes every record that install sent.
/// </summary>
public static class GameStatsClient
{
    /// <summary>Raised whenever what a record holds grows: the player is asked again (ADR 0011).</summary>
    public const int ConsentVersion = 1;

    /// <summary>Records sent in one request (the server takes up to 20).</summary>
    public const int MaxBatch = 20;

    /// <summary>The player agreed to send game records (this version of the list) and the pool is set up.</summary>
    public static bool Sharing(AppSettings settings) => settings.GameStatsConsent >= ConsentVersion && PoolClient.Available(settings);

    /// <summary>
    /// The install's id for its records: a keyed hash of the pool secret, never shown and never linked to an account.
    /// It lets the player have every record they sent deleted (<see cref="ForgetAsync"/>).
    /// </summary>
    public static string InstallId(AppSettings settings) => Hash("game-stats-install", PoolClient.Secret(settings));

    /// <summary>The record id of a game: a keyed hash of its history key, so a record sent twice is stored once.</summary>
    public static string RecordId(string gameKey, AppSettings settings) => Hash("game-stats|" + gameKey, PoolClient.Secret(settings));

    private static string Hash(string text, string secret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }

    internal static JsonObject Body(string installId, IEnumerable<JsonObject> records) => new()
    {
        ["v"] = GameStatsRecord.Version,
        ["iid"] = installId,
        ["games"] = new JsonArray(records.Select(r => (JsonNode?)r.DeepClone()).ToArray()),
    };

    /// <summary>Sends one batch of records.</summary>
    public static async Task<GameStatsAnswer> SubmitAsync(HttpClient http, string endpoint, string installId, IReadOnlyList<JsonObject> records, CancellationToken cancellationToken = default)
    {
        if (records.Count is 0 or > MaxBatch)
        {
            return records.Count == 0 ? GameStatsAnswer.Stored : GameStatsAnswer.Refused;
        }

        try
        {
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/events", Body(installId, records), cancellationToken).ConfigureAwait(false);
            var answer = AnswerOf(response.StatusCode);
            Diag.Info(answer == GameStatsAnswer.Stored
                ? $"Game stats: sent {records.Count} game(s)"
                : $"Game stats: the pool answered {(int)response.StatusCode} to {records.Count} game(s)");
            return answer;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Info($"Game stats: not sent now ({ex.GetType().Name})");
            return GameStatsAnswer.Later;
        }
    }

    internal static GameStatsAnswer AnswerOf(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => GameStatsAnswer.Stored,
        400 or 413 or 422 => GameStatsAnswer.Refused,
        _ => GameStatsAnswer.Later,
    };

    /// <summary>Asks the pool to delete every record this install sent; true once it answered so.</summary>
    public static async Task<bool> ForgetAsync(HttpClient http, string endpoint, string installId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(endpoint + "/v1/events/forget", new { iid = installId }, cancellationToken).ConfigureAwait(false);
            Diag.Info($"Game stats: asked the pool to delete this PC's games: {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Diag.Info($"Game stats: deleting the sent games waits ({ex.GetType().Name})");
            return false;
        }
    }
}
