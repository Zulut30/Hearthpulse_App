using System.Globalization;
using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// The copy of a replay to send to somebody (<c>docs/replay-library.md</c>): the replay through the import filter
/// (<see cref="ReplayImport.Sanitize"/>), so it has no names, BattleTags, account ids, ranks, ratings or legend number,
/// and only the date of play. Its name is neutral (<c>hearthpulse-replay-&lt;yyyyMMdd&gt;-&lt;mode&gt;.hpr</c>), not the
/// replay folder's one (that hashes the game key, which holds the BattleTag). Each copy gets a folder of its own under
/// the share folder, so two games of a day keep the same plain name; copies are deleted after <see cref="KeepFor"/>.
/// </summary>
public static class ReplayShare
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    /// <summary>The neutral file name of a shared copy.</summary>
    public static string FileName(ReplayHeader header)
    {
        var date = header.Summary is { StartedAt.Year: > 2000 } summary ? summary.StartedAt : DateTime.UtcNow.Date;
        var mode = header.Summary?.Mode is { Length: > 0 } m && m.All(char.IsAsciiLetter) ? m.ToLowerInvariant()
            : header.Kind == ReplayFile.Battlegrounds ? "battlegrounds" : "game";
        return $"hearthpulse-replay-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{mode}.hpr";
    }

    /// <summary>
    /// Writes the copy to share of <paramref name="text"/> under <paramref name="shareRoot"/> and returns its path, or null
    /// when the replay does not pass the filter or the copy could not be written. Old copies are deleted first.
    /// </summary>
    public static string? Prepare(string text, string shareRoot, CardDb cards)
    {
        CleanUp(shareRoot, DateTime.UtcNow);
        if (ReplayImport.Sanitize(text, cards, out _, dateOnly: true) is not { } clean || ReplayFile.Read(clean) is not { } file)
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(shareRoot, Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName(file.Header));
            using (var stream = File.Create(path))
            using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
            {
                gzip.Write(Encoding.UTF8.GetBytes(clean));
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Writing the replay copy to share failed", ex);
            return null;
        }
    }

    /// <summary>Deletes the copies older than <see cref="KeepFor"/>; returns how many folders went.</summary>
    public static int CleanUp(string shareRoot, DateTime nowUtc)
    {
        if (!Directory.Exists(shareRoot))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var folder in new DirectoryInfo(shareRoot).EnumerateDirectories())
        {
            if (folder.CreationTimeUtc > nowUtc - KeepFor)
            {
                continue;
            }

            try
            {
                folder.Delete(recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open in a chat app or an explorer window: next time.
                Diag.Debug($"Old share copy {folder.Name} not deleted: {ex.Message}");
            }
        }

        return deleted;
    }
}
