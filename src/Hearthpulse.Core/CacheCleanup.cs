namespace Hearthpulse.Core;

/// <summary>
/// Keeps the cache folder from growing without end; run once per start, off the UI thread. Only files the app itself
/// writes as a cache are removed: what is gone is downloaded again when needed.
/// </summary>
public static class CacheCleanup
{
    /// <summary>Card images kept per folder (art kind, render locale); the oldest downloads go first.</summary>
    public const int MaxImagesPerFolder = 2000;

    /// <summary>A temporary file older than this was left by a write that never finished (a crash, a full disk).</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    /// <summary>What a clean-up removed.</summary>
    public sealed record Result(int Files, long Bytes);

    /// <summary>Cleans <see cref="AppPaths.Cache"/>. Never throws.</summary>
    public static Result Run() => Run(AppPaths.Cache, DateTime.UtcNow);

    /// <summary>
    /// Removes the Battlegrounds meta cache of versions before the Hearthpulse API (<c>cache/meta</c>, up to 150 MB, no
    /// longer read), temporary files of unfinished writes, and the oldest card images beyond
    /// <see cref="MaxImagesPerFolder"/> in each image folder.
    /// </summary>
    public static Result Run(string cache, DateTime utcNow)
    {
        var files = 0;
        long bytes = 0;

        void Delete(FileInfo file)
        {
            try
            {
                var length = file.Length;
                file.Delete();
                files++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (another instance, an antivirus scan): the next start tries again.
            }
        }

        try
        {
            var root = new DirectoryInfo(cache);
            if (!root.Exists)
            {
                return new Result(0, 0);
            }

            var legacy = new DirectoryInfo(Path.Combine(cache, "meta"));
            if (legacy.Exists)
            {
                foreach (var file in legacy.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    Delete(file);
                }

                TryDeleteEmpty(legacy);
            }

            foreach (var temp in root.EnumerateFiles("*.tmp", SearchOption.AllDirectories))
            {
                if (utcNow - temp.LastWriteTimeUtc > OrphanAge)
                {
                    Delete(temp);
                }
            }

            var images = new DirectoryInfo(Path.Combine(cache, "images"));
            if (images.Exists)
            {
                foreach (var folder in images.EnumerateDirectories("*", SearchOption.AllDirectories).Prepend(images))
                {
                    foreach (var old in folder.EnumerateFiles().Where(f => f.Extension != ".tmp")
                                 .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxImagesPerFolder).ToList())
                    {
                        Delete(old);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Cache clean-up stopped", ex);
        }

        if (files > 0)
        {
            Diag.Info($"Cache clean-up removed {files} file(s), {bytes / 1048576.0:N1} MB");
        }

        return new Result(files, bytes);
    }

    private static void TryDeleteEmpty(DirectoryInfo dir)
    {
        try
        {
            dir.Delete(recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not empty (a file in use): left for the next start.
        }
    }
}
