using Hearthpulse.Core;

namespace Hearthpulse.Tests;

public sealed class CacheCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"hp-cache-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private string Write(string relative, int bytes, DateTime written)
    {
        var path = Path.Combine(_cache, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void Removes_the_old_meta_cache_orphaned_temp_files_and_the_oldest_images()
    {
        var keep = new[]
        {
            Write("cards.ruRU.json", 100, Now.AddDays(-1)),
            Write(Path.Combine("api", "bg-heroes-top_50_percent.json"), 100, Now.AddDays(-1)),
            Write(Path.Combine("api", "card-pool.json.0123.tmp"), 10, Now.AddMinutes(-5)), // a write in progress
        };
        var meta = Write(Path.Combine("meta", "comp-stats.json"), 1000, Now.AddDays(-3));
        Write(Path.Combine("meta", "cards-50.json.abc.tmp"), 500, Now.AddDays(-3));
        var orphan = Write(Path.Combine("api", "archetype-56-mulligan.json.0f0f.tmp"), 50, Now.AddHours(-2));
        var tiles = Enumerable.Range(0, CacheCleanup.MaxImagesPerFolder + 3)
            .Select(i => Write(Path.Combine("images", "tile", $"CARD_{i}.png"), 1, Now.AddMinutes(-i)))
            .ToList();
        var renders = Enumerable.Range(0, 5).Select(i => Write(Path.Combine("images", "card", "ruRU", $"CARD_{i}.png"), 1, Now.AddDays(-100))).ToList();

        var result = CacheCleanup.Run(_cache, Now);

        Assert.All(keep, f => Assert.True(File.Exists(f), f));
        Assert.False(Directory.Exists(Path.GetDirectoryName(meta)));
        Assert.False(File.Exists(orphan));
        // The three oldest tiles go; the newest stay.
        Assert.All(tiles.Take(CacheCleanup.MaxImagesPerFolder), f => Assert.True(File.Exists(f)));
        Assert.All(tiles.Skip(CacheCleanup.MaxImagesPerFolder), f => Assert.False(File.Exists(f)));
        Assert.All(renders, f => Assert.True(File.Exists(f)));
        Assert.Equal(new CacheCleanup.Result(6, 1000 + 500 + 50 + 3), result);

        // Nothing more to do on the next start.
        Assert.Equal(new CacheCleanup.Result(0, 0), CacheCleanup.Run(_cache, Now));
    }

    [Fact]
    public void A_missing_cache_folder_is_fine()
    {
        Assert.Equal(new CacheCleanup.Result(0, 0), CacheCleanup.Run(Path.Combine(_cache, "missing"), Now));
        Directory.CreateDirectory(_cache);
    }
}
