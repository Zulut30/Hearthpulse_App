using System.Text;
using Hearthlens.Core.Hearthstone;

namespace Hearthlens.Core.Logs;

public enum TailStart
{
    Beginning,
    LastGame,
    End,
}

/// <summary>Incrementally reads complete lines appended to one log file.</summary>
public sealed class LogTail
{
    private static readonly byte[] CreateGameMarker = Encoding.ASCII.GetBytes("GameState.DebugPrintPower() - CREATE_GAME");

    private readonly string _fileName;
    private string? _path;
    private long _position;
    private byte[] _remainder = Array.Empty<byte>();
    private byte[] _buffer = new byte[ReadChunkSize];

    /// <summary>
    /// How much is read per chunk. The buffer is reused, so a busy log does not put a fresh multi-megabyte array
    /// on the large object heap for every poll — that churn is what makes a low-memory PC stutter.
    /// </summary>
    private const int ReadChunkSize = 256 * 1024;

    public LogTail(string fileName)
    {
        _fileName = fileName;
    }

    public string FileName => _fileName;

    /// <summary>Byte offset of the next byte to read.</summary>
    public long Position => _position;

    /// <summary>Current size of the file, 0 when it does not exist.</summary>
    public long FileLength
    {
        get
        {
            try
            {
                return _path is not null && new FileInfo(_path) is { Exists: true } info ? info.Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    public void Reset(string sessionDir, TailStart start)
    {
        _path = Path.Combine(sessionDir, _fileName);
        _remainder = Array.Empty<byte>();
        _position = start switch
        {
            TailStart.LastGame when File.Exists(_path) => FindLastGameOffset(_path),
            TailStart.End when File.Exists(_path) => new FileInfo(_path).Length,
            _ => 0,
        };
    }

    /// <summary>Bytes appended to the file since the last read; 0 when the tail is caught up.</summary>
    public long PendingBytes
    {
        get
        {
            try
            {
                if (_path is null)
                {
                    return 0;
                }

                var info = new FileInfo(_path);
                return info.Exists ? Math.Max(0, info.Length - _position) : 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    public List<string> ReadNewLines(int maxBytes = 8 * 1024 * 1024)
    {
        var lines = new List<string>();
        if (_path is null || !File.Exists(_path))
        {
            return lines;
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _remainder = Array.Empty<byte>();
            }

            var available = stream.Length - _position;
            if (available <= 0)
            {
                return lines;
            }

            var toRead = (int)Math.Min(available, maxBytes);
            stream.Seek(_position, SeekOrigin.Begin);

            // The carry holds the bytes of an incomplete last line from the previous pass.
            var carry = _remainder.Length;
            EnsureBuffer(ReadChunkSize + carry);
            _remainder.CopyTo(_buffer, 0);

            var read = 0;
            while (read < toRead)
            {
                var want = Math.Min(ReadChunkSize, toRead - read);
                EnsureBuffer(carry + want);
                var got = stream.Read(_buffer, carry, want);
                if (got <= 0)
                {
                    break;
                }

                carry += got;
                read += got;

                var lineStart = 0;
                for (var i = 0; i < carry; i++)
                {
                    if (_buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    var end = i > lineStart && _buffer[i - 1] == (byte)'\r' ? i - 1 : i;
                    lines.Add(Encoding.UTF8.GetString(_buffer, lineStart, end - lineStart));
                    lineStart = i + 1;
                }

                if (lineStart > 0)
                {
                    Buffer.BlockCopy(_buffer, lineStart, _buffer, 0, carry - lineStart);
                    carry -= lineStart;
                }
            }

            _position += read;
            _remainder = carry > 0 ? _buffer[..carry] : Array.Empty<byte>();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return lines;
    }

    private void EnsureBuffer(int size)
    {
        if (_buffer.Length >= size)
        {
            return;
        }

        var grown = new byte[Math.Max(size, _buffer.Length * 2)];
        Array.Copy(_buffer, grown, _buffer.Length);
        _buffer = grown;
    }

    /// <summary>
    /// Offset of the line holding the last CREATE_GAME, scanning backwards in 1 MB blocks so a long session's
    /// log is never loaded into memory in one piece (a mid-game log can be hundreds of megabytes).
    /// </summary>
    private static long FindLastGameOffset(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            const int chunkSize = 1024 * 1024;
            var position = length;
            while (position > 0)
            {
                var size = (int)Math.Min(chunkSize, position);
                var start = position - size;
                // Read a little past the block so a marker split by the boundary is still seen whole.
                var count = (int)Math.Min(size + CreateGameMarker.Length - 1, length - start);
                var chunk = new byte[count];
                stream.Seek(start, SeekOrigin.Begin);
                stream.ReadExactly(chunk);

                var index = chunk.AsSpan().LastIndexOf(CreateGameMarker);
                if (index >= 0)
                {
                    return LineStart(stream, start + index);
                }

                position = start;
            }

            return length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Start of the line that contains <paramref name="offset"/>.</summary>
    private static long LineStart(FileStream stream, long offset)
    {
        var from = Math.Max(0, offset - 64 * 1024);
        var buffer = new byte[offset - from];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        var index = buffer.AsSpan().LastIndexOf((byte)'\n');
        return index < 0 ? from : from + index + 1;
    }
}

public interface ILogConsumer
{
    /// <summary>Called when the client starts a new log session (new folder).</summary>
    void OnSessionStarted(string sessionDir, DateTime sessionStart, bool attachedMidSession);

    /// <summary>A batch of new lines from one file. <paramref name="catchingUp"/> is true for history read at startup.</summary>
    void OnLines(string fileName, IReadOnlyList<string> lines, bool catchingUp);

    /// <summary>Called after each polling pass.</summary>
    void OnTick();

    /// <summary>
    /// The watcher is about to replay the current game from its start (hard refresh). The tracked state is rebuilt
    /// from the replayed lines, so it must be dropped here without recording the game as finished.
    /// </summary>
    void OnResync() { }
}

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
    ];

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

    public string? CurrentSession => _session;

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
        var size = PowerTail.FileLength;
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
        _thread = new Thread(Run) { IsBackground = true, Name = "Hearthlens log watcher" };
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

                _cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
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
                _firstSession = false;
                _session = latest;
                foreach (var tail in _tails)
                {
                    tail.Reset(latest, midSession && tail.FileName == "Power.log" ? TailStart.LastGame : TailStart.Beginning);
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
            pending += tail.PendingBytes;
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
