using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

/// <summary>
/// One shared Constructed game, exactly what the pool server stores: its Discover-style picks, the format and whether
/// it was won. No deck, class, rank, opponent, player name or account.
/// </summary>
public sealed record ConstructedPoolGame(string GameId, string Format, bool Won, IReadOnlyList<ConstructedPick> Picks)
{
    /// <summary>Day the game was played, in days since 1970-01-01 (UTC).</summary>
    public int Day { get; init; } = PoolGame.Today;

    /// <summary>
    /// The record for a finished Ranked or Casual Standard/Wild game with at least one pick and a win or a loss; null
    /// for anything else (Battlegrounds, Arena, Brawl, Friendly, Twist, ties, games without picks).
    /// </summary>
    public static ConstructedPoolGame? From(GameOutcome outcome, string secret)
    {
        var format = CardPool.DiscoverPicks.FormatOf(outcome.Format);
        if (format is null || outcome.GameType is not ("GT_RANKED" or "GT_CASUAL") || outcome.Result is not ("Win" or "Loss")
            || outcome.ConstructedPicks.Count == 0 || string.IsNullOrEmpty(outcome.Key))
        {
            return null;
        }

        return new ConstructedPoolGame(PoolGame.Id("constructed|" + outcome.Key, secret), format, outcome.Result == "Win", outcome.ConstructedPicks);
    }
}

/// <summary>How often one offered card was offered and taken from one source card, and the games won when taken.</summary>
public sealed record PickOption(int Offered, int Picked, int Wins);

/// <summary>The picks from one Discover-style card: how many choices it opened, and its offered cards by id.</summary>
public sealed record SourcePicks(int Choices, IReadOnlyDictionary<string, PickOption> Options);

/// <summary>The pool's pick totals of one format over its 30 day window (<c>GET /v1/choices</c>).</summary>
public sealed record DiscoverPickStats(string Format, int Games, IReadOnlyDictionary<string, SourcePicks> Sources)
{
    public static DiscoverPickStats Parse(JsonElement root)
    {
        var sources = new Dictionary<string, SourcePicks>(StringComparer.Ordinal);
        if (root.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "source") is not { Length: > 0 } source)
                {
                    continue;
                }

                var options = new Dictionary<string, PickOption>(StringComparer.Ordinal);
                if (item.TryGetProperty("options", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        // [card, offered, picked, wins]
                        if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 4 && row[0].ValueKind == JsonValueKind.String
                            && Count(row[1]) is { } offered && Count(row[2]) is { } picked && Count(row[3]) is { } wins)
                        {
                            options[row[0].GetString()!] = new PickOption(offered, Math.Min(picked, offered), Math.Min(wins, picked));
                        }
                    }
                }

                sources[source] = new SourcePicks(item.TryGetProperty("choices", out var c) ? Count(c) ?? 0 : 0, options);
            }
        }

        return new DiscoverPickStats(Text(root, "format") ?? string.Empty, root.TryGetProperty("games", out var g) ? Count(g) ?? 0 : 0, sources);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Count(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n >= 0 ? n : null;
}

/// <summary>
/// The pool's pick totals, loaded whole per format in the background and kept for <see cref="MaxAge"/>, so a Discover
/// never waits on the network and never tells the server which card is being played. <see cref="TryGet"/> answers from
/// memory. Without a pool address nothing is ever requested.
/// </summary>
public sealed class DiscoverPickSource(HttpClient http, Func<string> endpoint)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>A failed load is tried again after this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DiscoverPickStats Stats, DateTime At)> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.Ordinal);

    /// <summary>Raised on a worker thread when a format's totals arrived (the overlay builds its pick hints again).</summary>
    public event Action? Changed;

    /// <summary>The totals of "standard" or "wild" when loaded (null before); starts a load when there are none or they are old.</summary>
    public DiscoverPickStats? TryGet(string format)
    {
        var now = DateTime.UtcNow;
        var known = _known.TryGetValue(format, out var k) ? k : default;
        if (known.Stats is null || now - known.At > MaxAge)
        {
            Request(format, now);
        }

        return known.Stats;
    }

    private void Request(string format, DateTime now)
    {
        if (!endpoint().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || _asked.TryGetValue(format, out var at) && now - at < RetryAfter)
        {
            return;
        }

        _asked[format] = now;
        Task.Run(async () =>
        {
            if (await PoolClient.LoadChoicesAsync(http, endpoint(), format).ConfigureAwait(false) is { } stats)
            {
                _known[format] = (stats, DateTime.UtcNow);
                Changed?.Invoke();
            }
        }).Forget("Pool pick totals");
    }

    /// <summary>For tests and the render scenes: totals as if they had been loaded.</summary>
    public void Set(DiscoverPickStats stats)
    {
        _known[stats.Format] = (stats, DateTime.UtcNow);
        Changed?.Invoke();
    }
}
