using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Replay files on disk: one gzip file per game in the data folder's <c>replays</c> folder, named after the game key so
/// that recording the same game again (a log import) finds the file already there. The folder is kept under
/// <see cref="MaxBytes"/> and <see cref="MaxFiles"/>: the oldest games go first. Files of the first format
/// (<see cref="ReplayFile.OldMagic"/>) are deleted, not read: they could link a card sent into a deck with its draw.
/// </summary>
public sealed class ReplayStore(string directory)
{
    public const long MaxBytes = 300L * 1024 * 1024;
    public const int MaxFiles = 3000;

    /// <summary>A replay text larger than this is not saved (the event cap keeps real games far below it).</summary>
    private const int MaxTextChars = 32 * 1024 * 1024;

    /// <summary>The longest header line <see cref="ReadHeader"/> reads (real ones are under 2 KB).</summary>
    private const int MaxHeaderChars = 64 * 1024;

    private const string Extension = ".hpr";

    private int _oldFormatChecked;

    public string Directory { get; } = directory;

    public long MaxTotalBytes { get; init; } = MaxBytes;

    public int MaxTotalFiles { get; init; } = MaxFiles;

    /// <summary>
    /// Trim the folder after every save. A log import saves hundreds of games in a row: it turns this off and calls
    /// <see cref="Prune"/> once at the end.
    /// </summary>
    public bool AutoPrune { get; init; } = true;

    /// <summary>
    /// Trim by when each file was written, not by its game's start: Imported, where an old log's games are the newest
    /// files and must not go first.
    /// </summary>
    public bool PruneByWriteTime { get; init; }

    /// <summary>When a file was last written, or null when it is not there (the library's header cache keys on it).</summary>
    public DateTime? WrittenAt(string? fileName)
    {
        try
        {
            return PathOf(fileName) is { } path && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The file name of a game's replay: its start time and a hash of its key.</summary>
    public static string FileNameFor(string gameKey)
    {
        var started = gameKey.Split('|')[0];
        if (started.Length == 0 || !started.All(char.IsAsciiDigit))
        {
            started = "game";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(gameKey)))[..12];
        return $"{started}-{hash}{Extension}";
    }

    /// <summary>
    /// Saves a game's replay unless it is already saved (with <paramref name="overwrite"/>: replaces it, e.g. the part
    /// saved when the client left a game that then went on), then trims the folder. Returns the file name for the history
    /// row, or null when it could not be saved.
    /// </summary>
    public string? Save(string gameKey, string text, bool overwrite = false)
    {
        if (text.Length > MaxTextChars)
        {
            Diag.Info($"Replay of {gameKey} not saved: {text.Length:N0} characters");
            return null;
        }

        var name = FileNameFor(gameKey);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, name);
            // A file of an older format is replaced: a log import of the same game brings it to the current one.
            if (overwrite || !File.Exists(path) || !IsCurrentFormat(path))
            {
                // A temp name of its own: the live session and a log import may save the same game at the same time.
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                using (var file = File.Create(temp))
                using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                {
                    gzip.Write(Encoding.UTF8.GetBytes(text));
                }

                File.Move(temp, path, overwrite: true);
            }

            if (AutoPrune)
            {
                Prune(keep: name);
            }

            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Saving the replay of {gameKey} failed", ex);
            return null;
        }
    }

    /// <summary>The replay text of a saved file, or null when it is gone, unreadable or of the first format (then deleted).</summary>
    public string? Load(string? fileName)
    {
        if (PathOf(fileName) is not { } path)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string? text;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                // A file may come from outside (the library folders): unpacking stops at the size a replay may have.
                text = ReplayImport.ReadLimited(gzip, MaxTextChars);
            }

            if (text is null)
            {
                Diag.Info($"Replay {fileName} not read: over {MaxTextChars:N0} characters");
                return null;
            }

            if (text.StartsWith(ReplayFile.OldMagic, StringComparison.Ordinal))
            {
                File.Delete(path);
                return null;
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Diag.Error($"Reading replay {fileName} failed", ex);
            return null;
        }
    }

    /// <summary>
    /// The header of a saved file from its first two lines only (the library cards read it for every game), or null
    /// when the file is gone, unreadable or not a replay this version reads.
    /// </summary>
    public ReplayHeader? ReadHeader(string? fileName)
    {
        if (PathOf(fileName) is not { } path)
        {
            return null;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var magic = ReadLine(reader, 8);
            var json = magic is null ? null : ReadLine(reader, MaxHeaderChars);
            return json is null ? null : ReplayFile.Read($"{magic}\n{json}\n")?.Header;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>True when the folder holds <paramref name="fileName"/>.</summary>
    public bool Contains(string? fileName) => PathOf(fileName) is { } path && File.Exists(path);

    /// <summary>The replay file names in the folder, newest game first (by the start time in the name, as <see cref="Prune"/> orders them).</summary>
    public IReadOnlyList<string> Names()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(Directory).EnumerateFiles("*" + Extension).OrderByDescending(Age, StringComparer.Ordinal).Select(f => f.Name).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Listing replays in {Directory} failed", ex);
            return [];
        }
    }

    /// <summary>
    /// Copies a replay of another folder here under the same name (the star of the library). True when the copy is
    /// there afterwards. A copy already there is replaced when the source was written later (the part of a game saved
    /// when the client left, then the whole game after the reconnect).
    /// </summary>
    public bool CopyFrom(ReplayStore source, string fileName)
    {
        if (source.PathOf(fileName) is not { } from || PathOf(fileName) is not { } to)
        {
            return false;
        }

        try
        {
            if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from))
            {
                return true;
            }

            System.IO.Directory.CreateDirectory(Directory);
            var temp = $"{to}.{Guid.NewGuid():N}.tmp";
            File.Copy(from, temp);
            File.Move(temp, to, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Copying replay {fileName} to {Directory} failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Saves a ready replay text under <paramref name="fileName"/> (an imported game: the name comes from its content).
    /// False when it could not be saved; an existing file is left as it is.
    /// </summary>
    public bool SaveAs(string fileName, string text)
    {
        if (PathOf(fileName) is not { } path || text.Length > MaxTextChars)
        {
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (!File.Exists(path))
            {
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                using (var file = File.Create(temp))
                using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                {
                    gzip.Write(Encoding.UTF8.GetBytes(text));
                }

                File.Move(temp, path, overwrite: true);
            }

            if (AutoPrune)
            {
                Prune(keep: fileName);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Saving replay {fileName} to {Directory} failed", ex);
            return false;
        }
    }

    /// <summary>Deletes one replay (a game whose row went away). False when there was nothing to delete.</summary>
    public bool Delete(string? fileName)
    {
        if (PathOf(fileName) is not { } path)
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Deleting replay {fileName} failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Deletes the oldest replays until the folder fits the limits; <paramref name="keep"/> is never deleted. The first
    /// call also deletes files of the first format and temp files a crash left behind.
    /// </summary>
    public int Prune(string? keep = null)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return 0;
        }

        var deleted = 0;
        if (Interlocked.Exchange(ref _oldFormatChecked, 1) == 0)
        {
            deleted += DeleteLeftovers();
        }

        // Oldest by the game's start time in the name; a name without one ("game-") goes by when it was written.
        // Files written in one burst can share a write time (coarse clocks on macOS), so the name settles ties.
        var all = new DirectoryInfo(Directory).EnumerateFiles("*" + Extension);
        var files = (PruneByWriteTime
            ? all.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(Age, StringComparer.Ordinal)
            : all.OrderByDescending(Age, StringComparer.Ordinal)).ToList();
        long total = 0;
        var count = 0;
        foreach (var file in files)
        {
            total += file.Length;
            count++;
            if (file.Name == keep || (total <= MaxTotalBytes && count <= MaxTotalFiles))
            {
                continue;
            }

            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error($"Deleting old replay {file.Name} failed", ex);
            }
        }

        return deleted;
    }

    private int DeleteLeftovers()
    {
        var deleted = 0;
        var folder = new DirectoryInfo(Directory);
        foreach (var file in folder.EnumerateFiles("*" + Extension).Where(f => IsOldFormat(f.FullName))
                     .Concat(folder.EnumerateFiles("*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1))))
        {
            try
            {
                file.Delete();
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error($"Deleting replay leftover {file.Name} failed", ex);
            }
        }

        if (deleted > 0)
        {
            Diag.Info($"Deleted {deleted} replay files of the first format or left from a crash");
        }

        return deleted;
    }

    /// <summary>The game's start time in the name (yyyyMMddHHmmss); a name without one ("game-") goes by when it was written.</summary>
    private static string Age(FileInfo file) => file.Name.Length > 14 && file.Name[..14].All(char.IsAsciiDigit)
        ? file.Name[..14]
        : file.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One line of at most <paramref name="max"/> characters, or null when the line is longer or the text ends first.</summary>
    private static string? ReadLine(StreamReader reader, int max)
    {
        var line = new StringBuilder();
        while (reader.Read() is var c and >= 0)
        {
            if (c == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            if (line.Length >= max)
            {
                return null;
            }

            line.Append((char)c);
        }

        return null;
    }

    /// <summary>True for a file of the first format (its first line is <see cref="ReplayFile.OldMagic"/>).</summary>
    private static bool IsOldFormat(string path) => Head(path) == ReplayFile.OldMagic;

    /// <summary>True for a file of the format this version writes (an unreadable file counts as current: it is left alone).</summary>
    private static bool IsCurrentFormat(string path) => Head(path) is ReplayFile.Magic or null;

    /// <summary>The format line of a saved file, or null when it cannot be read.</summary>
    private static string? Head(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            Span<byte> head = stackalloc byte[4];
            return gzip.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 ? Encoding.ASCII.GetString(head) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The full path of a replay file name, or null for anything that is not a plain replay name.</summary>
    private string? PathOf(string? fileName) =>
        string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(Extension, StringComparison.Ordinal)
            ? null
            : Path.Combine(Directory, fileName);
}
