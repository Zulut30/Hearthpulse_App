using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// once: a subscriber of its section is never limited; a signed-in player is counted by the site; offline, signed out
/// or on any failure the game is counted on this machine against the last limit the site gave ("soft mode"). Until the
/// site has said that limits are on, nothing is limited. A game's id is stable (an HMAC of its start with a salt kept
/// here), so a restart mid-game asks about the same game; a reconnect (a new start while the last allowed game has not
/// finished) keeps that game's decision. Thread-safe; <see cref="Changed"/> fires when a decision arrives.
/// </summary>
public sealed class UsageGate(AccountClient client, AccountService? account, string path)
{
    /// <summary>The Battlegrounds stats pool in the overlay: comps, trinkets, tavern spells (our own players' games).</summary>
    public const string BgPoolStats = "bg.poolStats";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LimitsRefresh = TimeSpan.FromHours(6);

    /// <summary>A new game this soon after an allowed one that never finished is that game reconnected.</summary>
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromHours(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private Saved? _saved;
    private DateTime _limitsLoaded = DateTime.MinValue;

    public static string DefaultPath => Path.Combine(AppPaths.Root, "usage.json");

    public event Action? Changed;

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>How long the site may take to decide before the game is counted here.</summary>
    internal TimeSpan ClaimTimeout { get; init; } = TimeSpan.FromSeconds(5);

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
    /// Whether the feature's numbers may be shown in the game marked by <paramref name="game"/> (its start time). The
    /// first call of a game starts its claim and answers false until the decision arrives (<see cref="Changed"/>); a game
    /// the site does not limit, or one already decided today, answers at once.
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

            var saved = Today();
            var id = GameId(saved, game);
            var key = Key(feature, id);
            var known = saved.Decisions.GetValueOrDefault(key);
            if (_current.GetValueOrDefault(feature) == id || known is not null)
            {
                _current[feature] = id;
                return known?.Allowed ?? false;
            }

            _current[feature] = id;

            // A reconnect starts a new game in the log: it keeps the decision of the game it continues.
            if (saved.Open.GetValueOrDefault(feature) is { } open && UtcNow() - open.At < ReconnectWindow
                && saved.Decisions.GetValueOrDefault(Key(feature, open.Id)) is { Allowed: true } kept)
            {
                Save(saved with { Decisions = new(saved.Decisions, StringComparer.Ordinal) { [key] = kept } });
                return true;
            }

            current = new Game(feature, id);
        }

        // Counted here (signed out) the decision is ready at once; the site's arrives later.
        var claim = ClaimAsync(current);
        if (claim.IsCompleted)
        {
            lock (_gate)
            {
                return Today().Decisions.GetValueOrDefault(Key(feature, current.Id))?.Allowed ?? false;
            }
        }

        claim.Forget("Usage claim");
        return false;
    }

    /// <summary>The game ended: the next new game is a new game, not a reconnect.</summary>
    public void GameFinished()
    {
        lock (_gate)
        {
            var saved = Today();
            if (saved.Open.Count > 0)
            {
                Save(saved with { Open = new Dictionary<string, OpenGame>(StringComparer.Ordinal) });
            }
        }
    }

    /// <summary>The decision for the feature's current game (null: none yet, or the feature is not limited).</summary>
    public UsageClaim? Decision(string feature)
    {
        lock (_gate)
        {
            return Unlimited(feature) || _current.GetValueOrDefault(feature) is not { } id ? null : Today().Decisions.GetValueOrDefault(Key(feature, id));
        }
    }

    /// <summary>
    /// The line under the feature's numbers: what is left today while free games remain, or why the numbers are missing
    /// and where the subscription is once they are used up; empty for a subscriber, with no limits or before a decision.
    /// </summary>
    public string Note(string feature) => Decision(feature) switch
    {
        null or { Unlimited: true } => string.Empty,
        { Allowed: true } d => Loc.F("Usage.Limit.Left", Math.Max(0, d.Limit - d.Used), d.Limit),
        var d => Loc.F("Usage.Limit.Reached", d.Limit, ResetText(d.ResetsAt)),
    };

    /// <summary>When the UTC day ends, in the player's time ("03:00" in Moscow).</summary>
    internal static string ResetText(DateTime resetsAt) => resetsAt == DateTime.MinValue
        ? "00:00"
        : DateTime.SpecifyKind(resetsAt, DateTimeKind.Utc).ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    private bool Unlimited(string feature)
    {
        var limits = Load().Limits;
        return limits is not { Enabled: true } || !limits.Features.TryGetValue(feature, out var f)
            || (account?.Entitlements.Has(f.Section) ?? false);
    }

    private async Task ClaimAsync(Game game)
    {
        UsageClaim? claim = null;
        if (account is { SignedIn: true })
        {
            // A slow site must not hide the numbers for long: past a few seconds the game is counted here.
            using var timeout = new CancellationTokenSource(ClaimTimeout);
            try
            {
                claim = await account.ClaimUsageAsync(game.Feature, game.Id, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Any failure of the claim must not take the feature away: the game is counted here.
                Diag.Info($"usage: the claim failed ({ex.GetType().Name}), the game is counted here");
            }
        }

        lock (_gate)
        {
            claim ??= CountHere(game.Feature, game.Id);
            var saved = Today();
            var open = new Dictionary<string, OpenGame>(saved.Open, StringComparer.Ordinal);
            if (claim.Allowed)
            {
                open[game.Feature] = new OpenGame(game.Id, UtcNow());
            }

            Save(saved with
            {
                Decisions = new Dictionary<string, UsageClaim>(saved.Decisions, StringComparer.Ordinal) { [Key(game.Feature, game.Id)] = claim },
                Open = open,
            });
        }

        Changed?.Invoke();
    }

    /// <summary>Soft mode: the game is counted on this machine against the last known limit.</summary>
    private UsageClaim CountHere(string feature, string id)
    {
        var saved = Today();
        var limit = saved.Limits?.Features.GetValueOrDefault(feature)?.FreePerDay ?? int.MaxValue;
        var games = saved.Games.GetValueOrDefault(feature) ?? [];
        var resets = DateTime.SpecifyKind(UtcNow().Date.AddDays(1), DateTimeKind.Utc);
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

    /// <summary>
    /// The game's id: a UUID from an HMAC of its marker with this install's salt (kept here, never sent). The same game
    /// after a restart has the same id; the site cannot read the start time from it.
    /// </summary>
    private static string GameId(Saved saved, object game)
    {
        var text = game is DateTime start ? start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : game.ToString() ?? string.Empty;
        var hash = HMACSHA256.HashData(Convert.FromBase64String(saved.Salt), Encoding.UTF8.GetBytes(text));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8: a custom UUID
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    private static string Key(string feature, string id) => feature + "|" + id;

    /// <summary>The saved state, its games and decisions started over on a new UTC day (the salt stays).</summary>
    private Saved Today()
    {
        var saved = Load();
        var day = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return saved.Day == day ? saved : Save(saved with
        {
            Day = day,
            Games = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Decisions = new Dictionary<string, UsageClaim>(StringComparer.Ordinal),
            Open = new Dictionary<string, OpenGame>(StringComparer.Ordinal),
        });
    }

    private Saved Load()
    {
        if (_saved is not null)
        {
            return _saved;
        }

        Saved? read = null;
        try
        {
            read = File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Error("Reading the usage counts failed", ex);
        }

        // A file from before a field existed reads it as null: fill it, and make the salt once.
        return _saved = new Saved(
            read?.Limits,
            read?.Salt is { Length: > 0 } salt ? salt : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            read?.Day ?? string.Empty,
            read?.Games ?? new(StringComparer.Ordinal),
            read?.Decisions ?? new(StringComparer.Ordinal),
            read?.Open ?? new(StringComparer.Ordinal));
    }

    private Saved Save(Saved saved)
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

        return saved;
    }

    /// <summary>
    /// The last limits, this install's salt, today's UTC day, the games counted here, today's decisions by game and the
    /// last allowed game that has not finished, per feature. Ids are HMACs, never times or names.
    /// </summary>
    private sealed record Saved(
        UsageLimits? Limits,
        string Salt,
        string Day,
        Dictionary<string, List<string>> Games,
        Dictionary<string, UsageClaim> Decisions,
        Dictionary<string, OpenGame> Open);

    private sealed record OpenGame(string Id, DateTime At);

    private sealed record Game(string Feature, string Id);
}
