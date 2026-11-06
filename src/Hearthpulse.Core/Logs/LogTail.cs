using System.Text;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Core.Logs;

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
    private long _seenLength;
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

    /// <summary>Size of the file at the last <see cref="ReadNewLines"/> (0 when it was missing), without asking the disk.</summary>
    public long SeenLength => _seenLength;

    /// <summary>Bytes the last <see cref="ReadNewLines"/> saw but did not read (past its size limit), without asking the disk.</summary>
    public long SeenPendingBytes => Math.Max(0, _seenLength - _position);

    /// <summary>
    /// Points the tail at <paramref name="sessionDir"/>. Never throws: the watcher resets every tail and only then
    /// announces the session, so a log deleted or locked between the checks (the client rotating its folder) must
    /// not leave some tails on the old session. Such a log is read from its start.
    /// </summary>
    public void Reset(string sessionDir, TailStart start)
    {
        _path = Path.Combine(sessionDir, _fileName);
        _remainder = Array.Empty<byte>();
        _seenLength = 0;
        try
        {
            _position = start switch
            {
                TailStart.LastGame when File.Exists(_path) => FindLastGameOffset(_path),
                TailStart.End when new FileInfo(_path) is { Exists: true } info => info.Length,
                _ => 0,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _position = 0;
        }
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
        if (_path is null)
        {
            return lines;
        }

        try
        {
            // One look at the size first. The watcher polls four logs ten times a second all day, and opening each one
            // (plus the existence and size checks around it) was most of the app's CPU time while idle; a log that did
            // not grow is not opened. The size is live for a file the game holds open for writing.
            var info = new FileInfo(_path);
            _seenLength = info.Exists ? info.Length : 0;
            if (!info.Exists || _seenLength == _position)
            {
                return lines;
            }

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _seenLength = stream.Length;
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
