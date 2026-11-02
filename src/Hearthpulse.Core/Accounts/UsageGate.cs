using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

/// <summary>A limited feature: its key on the site, the subscription section that lifts the limit, free games a day.</summary>
public sealed record UsageFeature(string Section, int FreePerDay);

/// <summary>The site's limits (<c>GET usage/limits</c>): whether they are on, the features, when the UTC day ends.</summary>
public sealed record UsageLimits(bool Enabled, DateTime ResetsAt, IReadOnlyDictionary<string, UsageFeature> Features)
{
    internal static UsageLimits? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("enabled", out var enabled)
                || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new Dictionary<string, UsageFeature>(StringComparer.Ordinal);
            foreach (var f in features.EnumerateObject())
            {
                if (f.Value.ValueKind == JsonValueKind.Object && f.Value.TryGetProperty("section", out var section) && section.GetString() is { Length: > 0 } s
                    && f.Value.TryGetProperty("freePerDay", out var free) && free.TryGetInt32(out var n) && n >= 0)
                {
                    map[f.Name] = new UsageFeature(s, n);
                }
            }

            var resets = root.TryGetProperty("resetsAt", out var r) && r.TryGetDateTime(out var at) ? at.ToUniversalTime() : DateTime.MinValue;
            return new UsageLimits(enabled.GetBoolean(), resets, map);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The site's answer to one game of a limited feature (<c>POST usage/claim</c>).</summary>
public sealed record UsageClaim(bool Allowed, bool Unlimited, int Used, int Limit, DateTime ResetsAt)
{
    internal static UsageClaim? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("allowed", out var allowed) && allowed.ValueKind is JsonValueKind.True or JsonValueKind.False
                   && root.TryGetProperty("unlimited", out var unlimited) && unlimited.ValueKind is JsonValueKind.True or JsonValueKind.False
                   && root.TryGetProperty("used", out var used) && used.TryGetInt32(out var u)
                   && root.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var l)
                ? new UsageClaim(allowed.GetBoolean(), unlimited.GetBoolean(), u, l,
                    root.TryGetProperty("resetsAt", out var r) && r.TryGetDateTime(out var at) ? at.ToUniversalTime() : DateTime.MinValue)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Daily free use of the app's own paid analytics (plan 3.1, docs/usage-limits.md). A game of a limited feature counts
/// once: a subscriber of its section is never limited; a signed-in player is counted by the site; offline or signed out
/// the game is counted on this machine against the last limit the site gave ("soft mode"). Until the site has said that
/// limits are on, nothing is limited. Thread-safe; <see cref="Changed"/> fires when a decision arrives.
/// </summary>
public sealed class UsageGate(AccountClient client, AccountService? account, string path)
{
    /// <summary>The Battlegrounds stats pool in the overlay: comps, trinkets, tavern spells (our own players' games).</summary>
    public const string BgPoolStats = "bg.poolStats";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LimitsRefresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Game> _games = new(StringComparer.Ordinal);
    private Saved? _saved;
    private DateTime _limitsLoaded = DateTime.MinValue;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "usage.json");

    public event Action? Changed;

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The site's limits as last known (null: never answered, nothing is limited).</summary>
    public UsageLimits? Limits
    {
        get
        {
            lock (_gate)
            {
                return Load().Limits;
            }
        }
    }

    /// <summary>Asks the site for its limits now and then (every 6 hours at most); keeps the last answer on failure.</summary>
    public async Task RefreshLimitsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (UtcNow() - _limitsLoaded < LimitsRefresh)
            {
                return;
            }

            _limitsLoaded = UtcNow();
        }

        try
        {
            var (status, body) = await client.GetUsageLimitsAsync(cancellationToken).ConfigureAwait(false);
            if (status == 200 && UsageLimits.Parse(body) is { } limits)
            {
                lock (_gate)
                {
                    Save(Load() with { Limits = limits });
                }

                Changed?.Invoke();
            }
        }
        catch (AccountException ex)
        {
            Diag.Info($"usage: limits not loaded ({ex.Code})");
        }
    }

    /// <summary>
    /// Whether the feature's numbers may be shown in the game marked by <paramref name="game"/> (any value that changes
    /// with the game). The first call of a game starts its claim and answers false until the decision arrives
    /// (<see cref="Changed"/>); a game the site does not limit answers true at once.
    /// </summary>
    public bool Allow(string feature, object game)
    {
        Game current;
        lock (_gate)
        {
            if (Unlimited(feature))
            {
                return true;
            }

            if (_games.TryGetValue(feature, out var known) && Equals(known.Marker, game))
            {
                return known.Decision?.Allowed ?? false;
            }

            current = new Game(game, Guid.NewGuid().ToString("D"));
            _games[feature] = current;
        }

        // Counted here (signed out) the decision is ready at once; the site's arrives later.
        var claim = ClaimAsync(feature, current);
        if (claim.IsCompleted)
        {
            lock (_gate)
            {
                return current.Decision?.Allowed ?? false;
            }
        }

        claim.Forget("Usage claim");
        return false;
    }

    /// <summary>The decision for the feature's current game (null: none yet or not limited).</summary>
    public UsageClaim? Decision(string feature)
    {
        lock (_gate)
        {
            return _games.GetValueOrDefault(feature)?.Decision;
        }
    }

    /// <summary>The line shown instead of the feature's numbers when today's free games are used up; empty otherwise.</summary>
    public string Note(string feature) => Decision(feature) is { Allowed: false } d
        ? Loc.F("Usage.Limit.Reached", d.Limit, d.ResetsAt == DateTime.MinValue ? "00:00" : d.ResetsAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
        : string.Empty;

    private bool Unlimited(string feature)
    {
        var limits = Load().Limits;
        return limits is not { Enabled: true } || !limits.Features.TryGetValue(feature, out var f)
            || (account?.Entitlements.Has(f.Section) ?? false);
    }

    private async Task ClaimAsync(string feature, Game game)
    {
        UsageClaim? claim = null;
        if (account is { SignedIn: true })
        {
            // A slow site must not hide the numbers for long: past a few seconds the game is counted here.
            using var timeout = new CancellationTokenSource(ClaimTimeout);
            try
            {
                claim = await account.ClaimUsageAsync(feature, game.Id, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Diag.Info("usage: the site did not answer in time, the game is counted here");
            }
        }

        lock (_gate)
        {
            claim = claim is null ? CountHere(feature, game.Id) : Remember(feature, game.Id, claim);
            if (_games.GetValueOrDefault(feature) == game)
            {
                game.Decision = claim;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The site's decision; an allowed game is also counted here, so a later offline game sees today's count.</summary>
    private UsageClaim Remember(string feature, string id, UsageClaim claim)
    {
        if (claim is { Allowed: true, Unlimited: false })
        {
            var saved = Today();
            var games = saved.Games.GetValueOrDefault(feature) ?? [];
            if (!games.Contains(id, StringComparer.Ordinal))
            {
                // Keep as many as the site counted, so the local count never runs ahead of it.
                Save(saved with { Games = new Dictionary<string, List<string>>(saved.Games, StringComparer.Ordinal) { [feature] = [.. games.TakeLast(Math.Max(0, claim.Used - 1)), id] } });
            }
        }

        return claim;
    }

    /// <summary>Soft mode: the game is counted on this machine against the last known limit.</summary>
    private UsageClaim CountHere(string feature, string id)
    {
        var saved = Today();
        var limit = saved.Limits?.Features.GetValueOrDefault(feature)?.FreePerDay ?? int.MaxValue;
        var games = saved.Games.GetValueOrDefault(feature) ?? [];
        var resets = UtcNow().Date.AddDays(1);
        if (games.Contains(id, StringComparer.Ordinal))
        {
            return new UsageClaim(true, false, games.Count, limit, resets);
        }

        if (games.Count >= limit)
        {
            return new UsageClaim(false, false, games.Count, limit, resets);
        }

        Save(saved with { Games = new Dictionary<string, List<string>>(saved.Games, StringComparer.Ordinal) { [feature] = [.. games, id] } });
        return new UsageClaim(true, false, games.Count + 1, limit, resets);
    }

    /// <summary>The saved state, its game lists started over on a new UTC day.</summary>
    private Saved Today()
    {
        var saved = Load();
        var day = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return saved.Day == day ? saved : saved with { Day = day, Games = new Dictionary<string, List<string>>(StringComparer.Ordinal) };
    }

    private Saved Load()
    {
        if (_saved is not null)
        {
            return _saved;
        }

        try
        {
            _saved = File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Error("Reading the usage counts failed", ex);
        }

        return _saved ??= new Saved(null, string.Empty, new Dictionary<string, List<string>>(StringComparer.Ordinal));
    }

    private void Save(Saved saved)
    {
        _saved = saved;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Saving the usage counts failed", ex);
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                Diag.Info($"Leftover {temp}: {cleanup.Message}");
            }
        }
    }

    /// <summary>The last limits, today's UTC day and the games counted here per feature (random ids, never times).</summary>
    private sealed record Saved(UsageLimits? Limits, string Day, Dictionary<string, List<string>> Games);

    private sealed class Game(object marker, string id)
    {
        public object Marker { get; } = marker;

        public string Id { get; } = id;

        public UsageClaim? Decision { get; set; }
    }
}
