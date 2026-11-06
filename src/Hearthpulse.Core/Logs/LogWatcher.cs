using System.Text;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Core.Logs;

/// <summary>Polls the active Hearthstone log session folder and streams new lines to a consumer.</summary>
public sealed class LogWatcher : IDisposable
{
    private readonly ILogConsumer _consumer;
    private readonly Func<string?> _installDir;
    private readonly LogTail[] _tails =
    [
        new("LoadingScreen.log"),
        new("Decks.log"),
        new("Power.log"),
        new("Arena.log"),
    ];

    /// <summary>How often the logs are looked at; <see cref="QuietPollInterval"/> while the consumer expects nothing.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Ten looks a second at four logs were most of the app's CPU time while the game was closed; once a second still
    /// sees a game start long before its first game does.
    /// </summary>
    public static readonly TimeSpan QuietPollInterval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private string? _session;
    private DateTime _nextSessionCheck = DateTime.MinValue;
    private bool _firstSession = true;
    private int _resync;
    private long _lastPollTicks = Environment.TickCount64;
    private long _lastProgressTicks = Environment.TickCount64;
    private volatile string _stage = "starting";

    // Diagnostics: what the watcher did since the last heartbeat, and whether Power.log stopped growing.
    private long _nextHeartbeat;
    private long _linesSince;
    private int _batchesSince;
    private long _slowestPassMs;
    private long _lastPowerSize = -1;
    private long _powerSizeChangedAt = Environment.TickCount64;
    private bool _idleLogged;
    private volatile bool _capped;
    private long _pendingBytes;
    private volatile bool _stopped;

    public LogWatcher(ILogConsumer consumer, Func<string?> installDir)
    {
        _consumer = consumer;
        _installDir = installDir;
    }

    /// <summary>Milliseconds since the last polling pass finished (a dead or wedged watcher stops growing this).</summary>
    public long LastPollAgeMs => Environment.TickCount64 - Volatile.Read(ref _lastPollTicks);

    /// <summary>
    /// Milliseconds since the watcher last got anywhere: a finished pass or a delivered batch. A long replay keeps
    /// this small; only a pass that is stuck in one place lets it grow.
    /// </summary>
    public long LastProgressAgeMs => Environment.TickCount64 - Volatile.Read(ref _lastProgressTicks);

    /// <summary>What the watcher thread is doing, for the log when it gets stuck.</summary>
    public string Stage => _stage;

    /// <summary>
    /// True when Power.log stopped at Hearthstone's 10 MB log limit: the client writes nothing more this session, so
    /// tracking cannot continue until Hearthstone restarts (with the limit removed in client.config).
    /// </summary>
    public bool PowerLogCapped => _capped;

    private LogTail PowerTail => _tails[2];

    /// <summary>Notices a Power.log that stopped growing and reports the watcher's state every 15 seconds.</summary>
    private void Heartbeat(long pending)
    {
        // As seen by this pass's read: one more look at the disk per pass is what the read avoids.
        var size = PowerTail.SeenLength;
        var now = Environment.TickCount64;
        if (size != _lastPowerSize)
        {
            if (_idleLogged)
            {
                Diag.Info($"Power.log is growing again after {(now - _powerSizeChangedAt) / 1000} s (size {size:N0})");
            }

            _lastPowerSize = size;
            _powerSizeChangedAt = now;
            _idleLogged = false;
            _capped = false;
        }
        else if (!_idleLogged && size > 0 && now - _powerSizeChangedAt > 30_000)
        {
            _idleLogged = true;
            // The client checks the size every 100 writes, so a capped file ends a little past 10 MB.
            _capped = size is >= 10_000_000 and < 11_300_000 && ClientConfig.LimitActive(_installDir());
            Diag.Info(_capped
                ? $"Power.log stopped at {size:N0} bytes: Hearthstone's 10 MB log limit. The client writes nothing more until it restarts with Log.FileSizeLimit.Int=-1 in {ClientConfig.PathFor(_installDir())}."
                : $"Power.log has not changed for 30 s (size {size:N0}, read up to {PowerTail.Position:N0})");
        }

        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now + 15_000;
            Diag.Debug($"Log watcher: {Path.GetFileName(_session)} Power.log {size:N0} bytes, read {PowerTail.Position:N0}, pending {pending:N0}, "
                       + $"{_linesSince:N0} lines in {_batchesSince} batches, slowest pass {_slowestPassMs:N0} ms");
            _linesSince = 0;
            _batchesSince = 0;
            _slowestPassMs = 0;
        }
    }

    /// <summary>Bytes the game's logs have that the watcher has not delivered yet.</summary>
    public long PendingBytes => Volatile.Read(ref _pendingBytes);

    /// <summary>
    /// Makes the next pass re-read the log. With <paramref name="replayCurrentGame"/> the current game is replayed
    /// from its CREATE_GAME line, which rebuilds the whole board and turn (the recovery for a frozen overlay); without
    /// it every file just jumps to its end.
    /// </summary>
    public void RequestResync(bool replayCurrentGame) => Interlocked.Exchange(ref _resync, replayCurrentGame ? 1 : 2);

    public void Start()
    {
        Volatile.Write(ref _lastPollTicks, Environment.TickCount64);
        Volatile.Write(ref _lastProgressTicks, Environment.TickCount64);
        _thread = new Thread(Run) { IsBackground = true, Name = "Hearthpulse log watcher" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var passStarted = Environment.TickCount64;
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    Diag.Error("Log watcher pass failed", ex);
                }
                finally
                {
                    var took = Environment.TickCount64 - passStarted;
                    _slowestPassMs = Math.Max(_slowestPassMs, took);
                    if (took > 1000)
                    {
                        Diag.Info($"Slow log pass: {took:N0} ms, last step: {_stage}");
                    }

                    _stage = "waiting";
                    Volatile.Write(ref _lastPollTicks, Environment.TickCount64);
                    Volatile.Write(ref _lastProgressTicks, Environment.TickCount64);
                }

                _cts.Token.WaitHandle.WaitOne(_consumer.Quiet ? QuietPollInterval : PollInterval);
            }
        }
        catch (ObjectDisposedException)
        {
            // The watchdog restarted the watcher while this pass was still running: this thread is done.
        }
    }

    private void Poll()
    {
        if (_stopped)
        {
            return;
        }

        var resync = Interlocked.Exchange(ref _resync, 0);
        if (resync != 0)
        {
            _stage = "re-reading the current game";
            Resync(replayCurrentGame: resync == 1);
        }

        if (DateTime.UtcNow >= _nextSessionCheck)
        {
            _nextSessionCheck = DateTime.UtcNow.AddSeconds(2);
            var install = _installDir();
            var latest = install is null ? null : HearthstoneInstall.LatestLogSession(install);
            if (latest is not null && latest != _session)
            {
                // A session already running when we start is joined at its last game;
                // sessions created later are read from their first line.
                var midSession = _firstSession && File.Exists(Path.Combine(latest, "Power.log"));
                foreach (var tail in _tails)
                {
                    tail.Reset(latest, midSession && tail.FileName == "Power.log" ? TailStart.LastGame : TailStart.Beginning);
                }

                _firstSession = false;
                _session = latest;
                if (_stopped)
                {
                    // Replaced while looking for the session (a slow folder): the new watcher starts it.
                    return;
                }

                _consumer.OnSessionStarted(latest, HearthstoneInstall.SessionStart(latest) ?? DateTime.Now, midSession);
                Diag.Info($"Reading {Path.GetFileName(latest)}: Power.log {PowerTail.FileLength:N0} bytes, starting at {PowerTail.Position:N0}");
                var caughtUpFrom = Environment.TickCount64;
                CatchUp(midSession);
                Diag.Info($"Caught up with {Path.GetFileName(latest)} in {Environment.TickCount64 - caughtUpFrom:N0} ms");
            }
        }

        if (_session is null)
        {
            Volatile.Write(ref _pendingBytes, 0);
            _consumer.OnTick();
            return;
        }

        foreach (var tail in _tails)
        {
            _stage = "reading " + tail.FileName;
            var lines = tail.ReadNewLines();
            if (_stopped)
            {
                // The watcher was replaced while this pass ran: the old thread must not feed the consumer twice.
                return;
            }

            if (lines.Count > 0)
            {
                _stage = $"handling {lines.Count:N0} lines of {tail.FileName}";
                _linesSince += lines.Count;
                _batchesSince++;
                _consumer.OnLines(tail.FileName, lines, catchingUp: false);
                Volatile.Write(ref _lastProgressTicks, Environment.TickCount64);
            }
        }

        var pending = 0L;
        foreach (var tail in _tails)
        {
            pending += tail.SeenPendingBytes;
        }

        Volatile.Write(ref _pendingBytes, pending);
        Heartbeat(pending);
        _stage = "updating the view";
        _consumer.OnTick();
    }

    /// <summary>Re-attaches to the session folder and replays what is needed (see <see cref="RequestResync"/>).</summary>
    private void Resync(bool replayCurrentGame)
    {
        var install = _installDir();
        var latest = install is null ? null : HearthstoneInstall.LatestLogSession(install);
        if (latest is null)
        {
            return;
        }

        _session = latest;
        _firstSession = false;
        _nextSessionCheck = DateTime.UtcNow.AddSeconds(2);
        foreach (var tail in _tails)
        {
            if (replayCurrentGame && tail.FileName == "Power.log")
            {
                tail.Reset(latest, TailStart.LastGame);
            }
            else
            {
                tail.Reset(latest, TailStart.End);
            }
        }

        if (_stopped)
        {
            // Replaced while the tails were reset (a slow disk): the new watcher has replayed the game already, and
            // resetting it here would leave the overlay empty until the next game.
            return;
        }

        _consumer.OnResync();
        var size = PowerTail.FileLength;
        var from = PowerTail.Position;
        var started = Environment.TickCount64;
        CatchUp(historical: true);
        Diag.Info($"Resync replayed {size - from:N0} bytes of Power.log ({from:N0} to {size:N0}) in {Environment.TickCount64 - started:N0} ms");
        Diag.Info(replayCurrentGame
            ? $"Tracking resynced: replaying the current game from {Path.GetFileName(latest)}"
            : $"Tracking resynced: jumped to the end of {Path.GetFileName(latest)}");
    }

    private void CatchUp(bool historical)
    {
        foreach (var tail in _tails)
        {
            List<string> lines;
            while ((lines = tail.ReadNewLines()).Count > 0)
            {
                if (_stopped)
                {
                    return;
                }

                _stage = "catching up on " + tail.FileName;
                _consumer.OnLines(tail.FileName, lines, catchingUp: historical);
                Volatile.Write(ref _lastProgressTicks, Environment.TickCount64);
            }
        }
    }

    public void Dispose()
    {
        // Stop delivering first: a thread that was wedged (and is restarted by the watchdog) must not feed the
        // consumer a second time when it wakes up.
        _stopped = true;
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _cts.Dispose();
    }
}
