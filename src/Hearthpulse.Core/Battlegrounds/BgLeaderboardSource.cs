using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// The region's public Blizzard Battlegrounds leaderboard from the Hearthpulse API (<c>v1/leaderboards</c>, the
/// server's copy of Blizzard's table), downloaded whole, page by page, and cached on disk for a day
/// (docs/bg-opponent-mmr.md). Nothing is ever asked by player name: a lookup per opponent would tell the server whom
/// the player met (L-M2), so names are matched on this computer. <see cref="TryGet"/> answers from memory and never
/// waits; loads run on the thread pool and never throw.
/// </summary>
/// <param name="cacheDirectory">Where the tables are cached; the app's API cache folder by default (tests pass a scratch one).</param>
public sealed class BgLeaderboardSource(HearthpulseApi api, string? cacheDirectory = null)
{
    /// <summary>The server takes a new snapshot once a day: a cached table is asked for again after this long.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>How old a cached table may be and still stand in when the server cannot be reached.</summary>
    private static readonly TimeSpan MaxStale = TimeSpan.FromDays(7);

    /// <summary>A failed load is tried again after this long, not on every overlay update.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    /// <summary>The API's largest page.</summary>
    internal const int PageSize = 100;

    /// <summary>Bounds one load: EU has about 7100 rows (72 pages); a runaway answer stops at 30,000.</summary>
    internal const int MaxPages = 300;

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "battlegrounds", "battlegroundsduo" };

    private static readonly HashSet<string> Regions = new(StringComparer.Ordinal) { "EU", "US", "AP" };

    /// <summary>The snapshot day goes into the next pages' URL, so it must look like one.</summary>
    private static readonly Regex DayPattern = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");
    private readonly ConcurrentDictionary<string, (BgLeaderboard Board, DateTime At)> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loading = new(StringComparer.Ordinal);

    /// <summary>Raised on a worker thread when a load finished, found a table or not (the overlay builds its view again).</summary>
    public event Action? Changed;

    /// <summary>Now, for the in-memory age; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The leaderboard mode of a Battlegrounds game.</summary>
    public static string ModeOf(bool duos) => duos ? "battlegroundsduo" : "battlegrounds";

    /// <summary>
    /// The leaderboard region of the player's game account (<c>GameAccountId hi</c> of the local player in Power.log,
    /// bits 32-39): 1 Americas, 2 Europe, 3 Korea and 4 Taiwan (both play on the Asia-Pacific ladder). Null for
    /// China, which has no table here, and for an account the log did not give.
    /// </summary>
    public static string? RegionOf(ulong accountHi) => ((accountHi >> 32) & 0xFF) switch
    {
        1 => "US",
        2 => "EU",
        3 or 4 => "AP",
        _ => null,
    };

    /// <summary>
    /// The table of <paramref name="mode"/> in <paramref name="region"/> when it is in memory, else null; starts loading
    /// it (from the disk cache, else the server) when it is missing or a day old. <paramref name="loading"/> tells a table
    /// still on its way from one that could not be had (offline without a cached copy, an unknown region).
    /// </summary>
    public BgLeaderboard? TryGet(string mode, string region, out bool loading)
    {
        var key = mode + "|" + region;
        var now = Clock();
        var known = _known.TryGetValue(key, out var k) ? k : default;
        if ((known.Board is null || now - known.At > MaxAge) && Ask(key, now))
        {
            _loading[key] = 0;
            Task.Run(async () =>
            {
                try
                {
                    if (await LoadAsync(mode, region).ConfigureAwait(false) is { } board)
                    {
                        _known[key] = (board, Clock());
                    }
                }
                finally
                {
                    _loading.TryRemove(key, out _);
                    Changed?.Invoke();
                }
            }).Forget("Battlegrounds leaderboard");
        }

        loading = _loading.ContainsKey(key);
        return known.Board;
    }

    /// <summary>For tests and the render scenes: a table as if it had been loaded.</summary>
    public void Set(string mode, string region, BgLeaderboard board)
    {
        _known[mode + "|" + region] = (board, Clock());
        Changed?.Invoke();
    }

    /// <summary>
    /// The table from the disk cache when it is less than a day old, else from the server (then cached); a cached copy up
    /// to a week old when the server fails. Null when there is none, or the server has no such table (remembered for a
    /// day). Never throws.
    /// </summary>
    internal async Task<BgLeaderboard?> LoadAsync(string mode, string region, CancellationToken cancellationToken = default)
    {
        if (!Modes.Contains(mode) || !Regions.Contains(region))
        {
            return null;
        }

        var file = Path.Combine(_cacheDirectory, $"bg-leaderboard-{mode}-{region.ToLowerInvariant()}.json");
        var info = new FileInfo(file);
        var age = info.Exists ? DateTime.UtcNow - info.LastWriteTimeUtc : TimeSpan.MaxValue;
        BgLeaderboard? cached = null;
        if (info.Exists && TryReadCache(file, out cached) && age <= MaxAge)
        {
            return cached;
        }

        var (fetched, error) = await FetchAsync(mode, region, cancellationToken).ConfigureAwait(false);
        if (fetched is not null)
        {
            Diag.Info($"Battlegrounds leaderboard {mode} {region}: {fetched.Count} rows ({fetched.Day ?? "day unknown"})");
            await HearthpulseData.WriteCacheAsync(file, Serialize(mode, region, fetched), cancellationToken).ConfigureAwait(false);
            return fetched;
        }

        Diag.Info($"Battlegrounds leaderboard {mode} {region}: {error!.Code} {error.Message}");
        var stale = age <= MaxStale ? cached : null;
        if (stale is null && error.Code == "NOT_AVAILABLE")
        {
            // The server has no such table (404): remember that for a day instead of asking on every game.
            await HearthpulseData.WriteCacheAsync(file, Serialize(mode, region, null), cancellationToken).ConfigureAwait(false);
        }

        return stale;
    }

    /// <summary>Every page of the table, the later ones pinned to the first page's snapshot day so a new snapshot mid-way cannot mix two days.</summary>
    private async Task<(BgLeaderboard? Board, ApiError? Error)> FetchAsync(string mode, string region, CancellationToken cancellationToken)
    {
        var rows = new List<(string Name, int Rating)>();
        string? day = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var path = $"v1/leaderboards?mode={mode}&region={region}&limit={PageSize}&offset={page * PageSize}"
                       + (day is null ? string.Empty : "&day=" + day);
            var result = await api.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return (null, result.Error);
            }

            int count, total;
            try
            {
                var data = result.Data;
                if (page == 0 && data.TryGetProperty("snapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.Object
                    && snapshot.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String && DayPattern.IsMatch(d.GetString()!))
                {
                    day = d.GetString();
                }

                count = 0;
                foreach (var row in data.GetProperty("rows").EnumerateArray())
                {
                    count++;
                    if (row.ValueKind == JsonValueKind.Object
                        && row.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        && row.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Number)
                    {
                        rows.Add((name.GetString()!, (int)Math.Round(rating.GetDouble())));
                    }
                }

                total = data.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
            {
                return (null, new ApiError(200, "BAD_RESPONSE", "Unexpected leaderboard page."));
            }

            if (count < PageSize || (total > 0 && (page + 1) * PageSize >= total))
            {
                return (new BgLeaderboard(rows, day), null);
            }
        }

        Diag.Info($"Battlegrounds leaderboard {mode} {region}: stopped after {MaxPages} pages");
        return (new BgLeaderboard(rows, day), null);
    }

    /// <summary>Reads the cached table; <paramref name="board"/> is null for a remembered "no such table". False when the file is unreadable.</summary>
    private static bool TryReadCache(string file, out BgLeaderboard? board)
    {
        board = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("rows", out var list) || list.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            var rows = new List<(string, int)>();
            foreach (var row in list.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 2
                    && row[0].ValueKind == JsonValueKind.String && row[1].ValueKind == JsonValueKind.Number)
                {
                    rows.Add((row[0].GetString()!, row[1].GetInt32()));
                }
            }

            var day = root.TryGetProperty("day", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            board = new BgLeaderboard(rows, day);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Diag.Error($"Battlegrounds leaderboard cache unreadable ({Path.GetFileName(file)})", ex);
            return false;
        }
    }

    /// <summary>The cache file: the table's rows as [name, rating] pairs, or rows null for "the server has no such table".</summary>
    private static string Serialize(string mode, string region, BgLeaderboard? board)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("mode", mode);
            json.WriteString("region", region);
            if (board?.Day is { } day)
            {
                json.WriteString("day", day);
            }

            if (board is null)
            {
                json.WriteNull("rows");
            }
            else
            {
                json.WriteStartArray("rows");
                foreach (var (name, rating) in board.Rows)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(name);
                    json.WriteNumberValue(rating);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private bool Ask(string key, DateTime now)
    {
        if (_asked.TryGetValue(key, out var at) && now - at < RetryAfter)
        {
            return false;
        }

        _asked[key] = now;
        return true;
    }
}
