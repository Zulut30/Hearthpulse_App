using Hearthpulse.App.Infrastructure;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF card art's list of missing art: a miss is answered at once within its window and asked again after it, and
/// it is forgotten as soon as that art loads or a download client is set.
/// </summary>
public sealed class CardArtAbsentTests
{
    [Fact]
    public void A_missing_art_is_not_asked_again_within_the_window_and_is_after_it()
    {
        long now = 1_000;
        var misses = new ArtMisses(10_000, 16, () => now);

        Assert.False(misses.Skips("Portrait:A"));
        misses.Miss("Portrait:A");
        Assert.True(misses.Skips("Portrait:A"));
        Assert.False(misses.Skips("Portrait:B"));

        now += 9_999;
        Assert.True(misses.Skips("Portrait:A"));
        now += 1;
        Assert.False(misses.Skips("Portrait:A"));
        Assert.Equal(0, misses.Count);
    }

    [Fact]
    public void A_missing_art_is_forgotten_when_it_loads_and_the_list_starts_again_when_full()
    {
        long now = 0;
        var misses = new ArtMisses(10_000, 2, () => now);

        misses.Miss("Tile:A");
        misses.Found("Tile:A");
        Assert.False(misses.Skips("Tile:A"));

        misses.Miss("Tile:A");
        misses.Miss("Tile:B");
        misses.Miss("Tile:C");
        Assert.Equal(1, misses.Count);
        Assert.True(misses.Skips("Tile:C"));
        Assert.False(misses.Skips("Tile:A"));
    }

    [Fact]
    public void Setting_the_download_client_forgets_every_missing_art()
    {
        var http = CardArt.Http;
        CardArt.Absent.Miss("Portrait:CardArtAbsentTests");
        Assert.True(CardArt.Absent.Skips("Portrait:CardArtAbsentTests"));

        CardArt.Http = http;

        Assert.False(CardArt.Absent.Skips("Portrait:CardArtAbsentTests"));
    }
}
