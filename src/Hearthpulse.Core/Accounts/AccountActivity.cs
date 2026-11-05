using System.Text.Json;

namespace Hearthpulse.Core.Accounts;

/// <summary>The app's calls to hearthpulse.net, as the account center groups them.</summary>
public enum SiteService
{
    /// <summary>The sign-in: <c>/me</c>, token refresh, the session restored at start.</summary>
    Account,

    /// <summary>Finished games to the profile (<c>tracker/events/batch</c>).</summary>
    Games,

    /// <summary>Replays uploaded to the site and deleted from it (<c>tracker/replays</c>).</summary>
    Replays,

    /// <summary>The daily free limits (<c>usage/limits</c>, <c>usage/claim</c>).</summary>
    Limits,

    /// <summary>Crash reports (<c>crash-reports</c>).</summary>
    CrashReports,
}

/// <summary>
/// One service's last contact with the site: when it last worked, the last error code and when it happened, and (for
/// the games) when the next attempt is due.
/// </summary>
public sealed record SiteServiceRecord(DateTime? LastSuccess, string? LastError, DateTime? LastErrorAt, DateTime? NextTry)
{
    public static readonly SiteServiceRecord Empty = new(null, null, null, null);

    /// <summary>The last word from this service is an error (none worked since).</summary>
    public bool Failing => LastErrorAt is { } error && (LastSuccess is not { } success || error > success);
}

/// <summary>
/// Today's use of a limited feature (from <see cref="UsageGate"/>): whether the site limits it, the subscription section
/// that lifts the limit, games used and allowed today, and when the UTC day ends. <see cref="Day"/> is the UTC day.
/// </summary>
public sealed record UsageToday(string Day, bool Enabled, string Section, int Used, int Limit, DateTime ResetsAt);

/// <summary>
/// The journal of the app's contact with hearthpulse.net for the account center (Settings → Account): per
/// <see cref="SiteService"/> the last success and the last error, plus the games sent to the profile, refused, and
/// stored without statistics, kept across restarts in <c>account-activity.json</c> in the data folder. Nothing in it
/// goes to the site. The game counters and the games' record belong to one account (<see cref="BindAccount"/>): another
/// account signing in starts them over. Thread-safe; changes in a burst are written once, a moment later and outside
/// the lock (<see cref="Flush"/> writes now), through a temporary file; a broken or unreadable file starts over; it never
/// throws to its callers. <see cref="Changed"/> fires (on any thread) after every change.
/// </summary>
public sealed class AccountActivity
{
    /// <summary>How long a change waits for the next one before the journal is written (a burst is one write).</summary>
    internal static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest error code kept; a longer one is cut to this.</summary>
    internal const int MaxCodeLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    // One file write at a time, in order (taken before _gate, never inside it).
    private readonly Lock _fileGate = new();
    private readonly string? _path;
    private Saved? _saved;
    private long _version;
    private long _written;
    private ITimer? _writeTimer;
    private int _writes;

    /// <param name="path">The journal file; null keeps it in memory only (tests, a service built without one).</param>
    public AccountActivity(string? path)
    {
        _path = path;
        if (path is not null)
        {
            // A change made in the last moment before the app closes still reaches the file.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    public event Action? Changed;

    /// <summary>The clock; tests move it.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The timer behind the delayed write; tests replace it.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How many times the journal went to the file (tests check that a burst is one write).</summary>
    internal int Writes => Volatile.Read(ref _writes);

    /// <summary>The account the counters and the games' record belong to (the site's user id); null before any.</summary>
    public string? AccountId => Read(s => s.AccountId);

    public SiteServiceRecord this[SiteService service]
    {
        get
        {
            lock (_gate)
            {
                return Load().Services.GetValueOrDefault(service.ToString()) ?? SiteServiceRecord.Empty;
            }
        }
    }

    /// <summary>Games the site accepted for the profile, ever (on this machine).</summary>
    public long GamesSent => Read(s => s.GamesSent);

    /// <summary>Games the site refused for good.</summary>
    public long GamesRefused => Read(s => s.GamesRefused);

    /// <summary>Games the site stored without statistics (warnings).</summary>
    public long GamesWithoutStatistics => Read(s => s.GamesWithoutStatistics);

    /// <summary>When the site last accepted a game (UTC).</summary>
    public DateTime? LastGameSentAt => Read(s => s.LastGameSentAt);

    /// <summary>Today's use of the overlay's limited stats, as <see cref="UsageGate"/> last saw it; null before any.</summary>
    public UsageToday? Usage => Read(s => s.Usage);

    /// <summary>The service answered as expected.</summary>
    public void Succeeded(SiteService service) =>
        Change(s => s with { Services = With(s.Services, service, Get(s, service) with { LastSuccess = UtcNow(), NextTry = null }) });

    /// <summary>The service failed with <paramref name="code"/>; <paramref name="nextTry"/> is when it tries again, if known.</summary>
    public void Failed(SiteService service, string code, DateTime? nextTry = null)
    {
        var kept = CleanCode(code);
        Change(s => s with { Services = With(s.Services, service, Get(s, service) with { LastError = kept, LastErrorAt = UtcNow(), NextTry = nextTry }) });
    }

    /// <summary>
    /// The account now signed in (the site's user id). Another account than the one the journal counted for starts the
    /// game counters and the games' record over; a journal from before accounts were recorded is taken as this one's.
    /// </summary>
    public void BindAccount(string accountId)
    {
        if (accountId.Length == 0 || Read(s => s.AccountId) == accountId)
        {
            return;
        }

        Change(s => s.AccountId is null || s.AccountId == accountId
            ? s with { AccountId = accountId }
            : s with
            {
                AccountId = accountId,
                Services = new Dictionary<string, SiteServiceRecord>(s.Services.Where(p => p.Key != nameof(SiteService.Games)), StringComparer.Ordinal),
                GamesSent = 0,
                GamesRefused = 0,
                GamesWithoutStatistics = 0,
                LastGameSentAt = null,
            });
    }

    /// <summary>Writes a change still waiting for its delayed write now (closing the app, tests).</summary>
    public void Flush()
    {
        lock (_fileGate)
        {
            Saved saved;
            long version;
            lock (_gate)
            {
                _writeTimer?.Dispose();
                _writeTimer = null;
                if (_saved is null || _version == _written)
                {
                    return;
                }

                (saved, version) = (_saved, _version);
            }

            // Outside the lock: the record is immutable, and readers do not wait for the disk.
            Write(saved);
            Interlocked.Increment(ref _writes);
            lock (_gate)
            {
                _written = version;
            }
        }
    }

    /// <summary>
    /// A code as the journal keeps it: at most <see cref="MaxCodeLength"/> characters of A-Z, 0-9, <c>_ . -</c> (letters
    /// in upper case); anything else is <c>OTHER</c>, so a site's answer cannot put arbitrary text in the file or the card.
    /// </summary>
    internal static string CleanCode(string? code)
    {
        if (code is not { Length: > 0 })
        {
            return "OTHER";
        }

        var cut = (code.Length > MaxCodeLength ? code[..MaxCodeLength] : code).ToUpperInvariant();
        return cut.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-') ? cut : "OTHER";
    }

    /// <summary>Forgets the service's last error (the player signed out: an ended sign-in is no longer news).</summary>
    public void ClearFailure(SiteService service) =>
        Change(s => s with { Services = With(s.Services, service, Get(s, service) with { LastError = null, LastErrorAt = null, NextTry = null }) });

    /// <summary>A batch answer: games accepted, refused and stored without statistics.</summary>
    public void CountGames(int accepted, int refused, int withoutStatistics)
    {
        if (accepted <= 0 && refused <= 0 && withoutStatistics <= 0)
        {
            return;
        }

        Change(s => s with
        {
            GamesSent = s.GamesSent + Math.Max(0, accepted),
            GamesRefused = s.GamesRefused + Math.Max(0, refused),
            GamesWithoutStatistics = s.GamesWithoutStatistics + Math.Max(0, withoutStatistics),
            LastGameSentAt = accepted > 0 ? UtcNow() : s.LastGameSentAt,
        });
    }

    /// <summary>Today's use of the limited feature, from the usage gate.</summary>
    public void SetUsage(UsageToday usage)
    {
        if (Read(s => s.Usage) != usage)
        {
            Change(s => s with { Usage = usage });
        }
    }

    private T Read<T>(Func<Saved, T> read)
    {
        lock (_gate)
        {
            return read(Load());
        }
    }

    private static SiteServiceRecord Get(Saved saved, SiteService service) =>
        saved.Services.GetValueOrDefault(service.ToString()) ?? SiteServiceRecord.Empty;

    private static Dictionary<string, SiteServiceRecord> With(Dictionary<string, SiteServiceRecord> services, SiteService service, SiteServiceRecord record) =>
        new(services, StringComparer.Ordinal) { [service.ToString()] = record };

    private void Change(Func<Saved, Saved> change)
    {
        lock (_gate)
        {
            _saved = change(Load());
            _version++;
            // The first change of a burst starts the timer; the ones after it ride along in the same write.
            if (_path is not null && _writeTimer is null)
            {
                _writeTimer = Time.CreateTimer(_ => Flush(), null, WriteDelay, Timeout.InfiniteTimeSpan);
            }
        }

        Changed?.Invoke();
    }

    private Saved Load()
    {
        if (_saved is not null)
        {
            return _saved;
        }

        Saved? read = null;
        if (_path is not null)
        {
            try
            {
                read = File.Exists(_path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path), Json) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // A broken journal is only lost history: start over.
                Diag.Error("Reading the account activity failed", ex);
            }
        }

        // A file from before a field existed reads it as null; a stored code is cleaned like a new one.
        return _saved = new Saved(
            new Dictionary<string, SiteServiceRecord>(
                read?.Services?.Where(p => p.Value is not null)
                    .Select(p => KeyValuePair.Create(p.Key, p.Value.LastError is null ? p.Value : p.Value with { LastError = CleanCode(p.Value.LastError) })) ?? [],
                StringComparer.Ordinal),
            Math.Max(0, read?.GamesSent ?? 0),
            Math.Max(0, read?.GamesRefused ?? 0),
            Math.Max(0, read?.GamesWithoutStatistics ?? 0),
            read?.LastGameSentAt,
            read?.Usage,
            read?.AccountId is { Length: > 0 } id ? id : null);
    }

    private void Write(Saved saved)
    {
        if (_path is null)
        {
            return;
        }

        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Diag.Error("Saving the account activity failed", ex);
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

    /// <summary>
    /// The journal on disk: services by name, the game counters, today's use of the limited feature, and the account the
    /// counters belong to.
    /// </summary>
    private sealed record Saved(
        Dictionary<string, SiteServiceRecord> Services,
        long GamesSent,
        long GamesRefused,
        long GamesWithoutStatistics,
        DateTime? LastGameSentAt,
        UsageToday? Usage,
        string? AccountId = null);
}
