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

    private const string Extension = ".hpr";

    private int _oldFormatChecked;

    public static ReplayStore Default { get; } = new(AppPaths.Replays);

    public string Directory { get; } = directory;

    public long MaxTotalBytes { get; init; } = MaxBytes;

    public int MaxTotalFiles { get; init; } = MaxFiles;

    /// <summary>
    /// Trim the folder after every save. A log import saves hundreds of games in a row: it turns this off and calls
    /// <see cref="Prune"/> once at the end.
    /// </summary>
    public bool AutoPrune { get; init; } = true;

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

            string text;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
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
        var files = new DirectoryInfo(Directory).EnumerateFiles("*" + Extension)
            .OrderByDescending(f => f.Name.Length > 14 && f.Name[..14].All(char.IsAsciiDigit)
                ? f.Name[..14]
                : f.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToList();
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
