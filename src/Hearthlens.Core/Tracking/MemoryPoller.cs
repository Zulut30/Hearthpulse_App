using System.Diagnostics;
using Hearthlens.Core.Memory;

namespace Hearthlens.Core.Tracking;

/// <summary>What the tracking thread wants read from the game on the next memory scan.</summary>
internal sealed record MemoryRequest(
    int Generation,
    bool InGame,
    bool Battlegrounds,
    bool ReadRating,
    bool WantRaces,
    bool WantRanks,
    string? AwaitingRatingKey);

/// <summary>What one memory scan found; null where nothing was read.</summary>
internal sealed record MemoryReading(
    MemoryRequest Request,
    BattlegroundsRating? Rating,
    int? PostGameRating,
    IReadOnlyList<int>? Races,
    BattlegroundsButtonCosts? ButtonCosts,
    (MatchPlayerInfo? Local, MatchPlayerInfo? Opponent)? Players);

/// <summary>
/// Reads the game's memory on its own thread. A scan can take seconds on a slow PC, or when a security tool inspects
/// every read; on the log thread that froze the overlay. The tracking thread posts what it needs and picks up the
/// newest reading, it never waits for a scan.
/// </summary>
internal sealed class MemoryPoller
{
    private readonly HearthstoneMemory _memory;
    private readonly AutoResetEvent _wake = new(false);
    private MemoryRequest? _request;
    private MemoryReading? _reading;
    private Thread? _thread;

    public MemoryPoller(HearthstoneMemory memory)
    {
        _memory = memory;
    }

    /// <summary>Sets what the next scan reads; null pauses scanning (Hearthstone is not running).</summary>
    public void Post(MemoryRequest? request)
    {
        Volatile.Write(ref _request, request);
        if (request is not null && _thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Hearthlens memory reader" };
            _thread.Start();
        }
    }

    /// <summary>Scans right away instead of waiting for the next interval.</summary>
    public void Wake() => _wake.Set();

    /// <summary>The newest reading that was not taken yet, or null.</summary>
    public MemoryReading? Take() => Interlocked.Exchange(ref _reading, null);

    private void Run()
    {
        while (true)
        {
            var request = Volatile.Read(ref _request);
            var delay = TimeSpan.FromSeconds(3);
            if (request is not null)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    if (Scan(request) is { } reading)
                    {
                        Volatile.Write(ref _reading, reading);
                    }
                }
                catch (Exception ex)
                {
                    Diag.Error("Memory scan failed", ex);
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed > TimeSpan.FromSeconds(1))
                {
                    Diag.Info($"Memory scan took {elapsed.TotalMilliseconds:N0} ms; the next one waits longer");
                    delay = TimeSpan.FromSeconds(10);
                }
                else if (request.AwaitingRatingKey is not null)
                {
                    delay = TimeSpan.FromSeconds(1);
                }
            }

            _wake.WaitOne(delay);
        }
    }

    private MemoryReading? Scan(MemoryRequest request)
    {
        if (!_memory.EnsureAttached())
        {
            return null;
        }

        var awaiting = request.AwaitingRatingKey is not null;
        var battlegroundsGame = request.InGame && request.Battlegrounds;
        return new MemoryReading(
            request,
            request.ReadRating || awaiting ? _memory.GetBattlegroundsRating() : null,
            awaiting ? _memory.GetPostGameBattlegroundsRating() : null,
            battlegroundsGame && request.WantRaces ? _memory.GetBattlegroundsRaces() : null,
            battlegroundsGame ? _memory.GetBattlegroundsButtonCosts() : null,
            request.InGame && request.WantRanks ? _memory.GetMatchPlayers() : null);
    }
}
