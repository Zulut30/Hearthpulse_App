using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Replay files on disk: one gzip file per game in the data folder's <c>replays</c> folder, named after the game key so
/// that recording the same game again (a log import) finds the file already there. The folder is kept under
/// <see cref="MaxBytes"/> and <see cref="MaxFiles"/>: the oldest replays go first.
/// </summary>
public sealed class ReplayStore(string directory)
{
    public const long MaxBytes = 300L * 1024 * 1024;
    public const int MaxFiles = 3000;

    /// <summary>A replay text larger than this is not saved (the event cap keeps real games far below it).</summary>
    private const int MaxTextChars = 32 * 1024 * 1024;

    private const string Extension = ".hpr";

    public static ReplayStore Default { get; } = new(AppPaths.Replays);

    public string Directory { get; } = directory;

    public long MaxTotalBytes { get; init; } = MaxBytes;

    public int MaxTotalFiles { get; init; } = MaxFiles;

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
            if (overwrite || !File.Exists(path))
            {
                var temp = path + ".tmp";
                using (var file = File.Create(temp))
                using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                {
                    gzip.Write(Encoding.UTF8.GetBytes(text));
                }

                File.Move(temp, path, overwrite: true);
            }

            Prune(keep: name);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Saving the replay of {gameKey} failed", ex);
            return null;
        }
    }

    /// <summary>The replay text of a saved file, or null when it is gone or unreadable.</summary>
    public string? Load(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName != Path.GetFileName(fileName) || !fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.Combine(Directory, fileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Diag.Error($"Reading replay {fileName} failed", ex);
            return null;
        }
    }

    /// <summary>Deletes the oldest replays until the folder fits the limits; <paramref name="keep"/> is never deleted.</summary>
    public int Prune(string? keep = null)
    {
        var files = new DirectoryInfo(Directory).EnumerateFiles("*" + Extension)
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ThenByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        long total = 0;
        var count = 0;
        var deleted = 0;
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
}
