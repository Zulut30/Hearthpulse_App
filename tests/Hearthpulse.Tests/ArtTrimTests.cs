using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

public class ArtTrimTests
{
    private const int Side = 256;

    /// <summary>A 256x256 BGRA sheet: white paper with a dark illustration in the given rectangle.</summary>
    private static byte[] Sheet(int artX, int artY, int artWidth, int artHeight, Func<int, int, byte>? paper = null)
    {
        var pixels = new byte[Side * Side * 4];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var art = x >= artX && x < artX + artWidth && y >= artY && y < artY + artHeight;
                var level = art ? (byte)((x + y) % 120) : paper?.Invoke(x, y) ?? 255;
                var at = ((y * Side) + x) * 4;
                pixels[at] = level;
                pixels[at + 1] = level;
                pixels[at + 2] = level;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    private static ArtCrop? Find(byte[] pixels) => ArtTrim.FindSquare(pixels, Side, Side, Side * 4);

    [Fact]
    public void SideMarginsAreCutToASquareInsideTheArt()
    {
        // The usual shape: white columns left and right, a few white rows at the top and bottom.
        var crop = Find(Sheet(38, 5, 186, 248));

        Assert.NotNull(crop);
        var c = crop.Value;
        Assert.True(c.X >= 38 && c.X + c.Size <= 38 + 186, $"{c}");
        Assert.True(c.Y >= 5 && c.Y + c.Size <= 5 + 248, $"{c}");
        Assert.InRange(c.Size, 180, 186);
        // Centred on the art.
        Assert.InRange(c.Y + (c.Size / 2) - (5 + 124), -2, 2);
    }

    [Fact]
    public void TopAndBottomBandsAreCutAndTheSquareCentredSideways()
    {
        var crop = Find(Sheet(0, 24, Side, 208));

        Assert.NotNull(crop);
        var c = crop.Value;
        Assert.True(c.Y >= 24 && c.Y + c.Size <= 24 + 208, $"{c}");
        Assert.InRange(c.X + (c.Size / 2), 126, 130);
    }

    [Fact]
    public void JpegNoiseInThePaperStillCountsAsMargin()
    {
        var crop = Find(Sheet(32, 0, 192, Side, (x, y) => (byte)(240 + ((x * 7 + y * 3) % 16))));

        Assert.NotNull(crop);
        Assert.True(crop.Value.X >= 32 && crop.Value.X + crop.Value.Size <= 224, $"{crop}");
    }

    [Fact]
    public void ArtWithoutMarginsIsLeftAlone() => Assert.Null(Find(Sheet(0, 0, Side, Side)));

    [Fact]
    public void LightArtIsLeftAlone()
    {
        Assert.Null(Find(Sheet(0, 0, 0, 0)));
        // Mostly white with a small dark mark: the margins cannot be told from the art.
        Assert.Null(Find(Sheet(100, 100, 40, 40)));
    }

    /// <summary>A 256x59 deck tile: dark art with a white block over the given columns.</summary>
    private static byte[] Tile(int blockFrom, int blockTo)
    {
        var pixels = new byte[256 * 59 * 4];
        for (var y = 0; y < 59; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var level = x >= blockFrom && x < blockTo ? (byte)(250 + (x % 5)) : (byte)(40 + ((x * y) % 90));
                var at = ((y * 256) + x) * 4;
                pixels[at] = level;
                pixels[at + 1] = level;
                pixels[at + 2] = level;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    [Fact]
    public void TileWhiteBlockIsFoundAndFadedAway()
    {
        // The shape of the new sets' tiles: a little art, then a white block, then the art.
        var pixels = Tile(19, 88);
        var start = ArtTrim.FindTileArtStart(pixels, 256, 59, 256 * 4);

        Assert.InRange(start ?? -1, 88, 92);
        ArtTrim.FadeTileStart(pixels, 256, 59, 256 * 4, start!.Value, premultiplied: false);
        for (var x = 0; x < 256; x++)
        {
            var at = ((30 * 256) + x) * 4;
            if (x < start)
            {
                Assert.Equal(0, pixels[at + 3]);
                Assert.Equal(0, pixels[at]);
            }
            else if (x > start + 20)
            {
                Assert.Equal(255, pixels[at + 3]);
            }
        }

        // The art fades in rather than starting at a hard edge.
        Assert.InRange(pixels[((30 * 256) + start.Value + 8) * 4 + 3], 60, 200);
    }

    [Fact]
    public void TileBlockAtTheLeftEdgeIsFound() =>
        Assert.InRange(ArtTrim.FindTileArtStart(Tile(0, 34), 256, 59, 256 * 4) ?? -1, 34, 38);

    [Fact]
    public void TilesWithoutABlockAreLeftAlone()
    {
        Assert.Null(ArtTrim.FindTileArtStart(Tile(0, 0), 256, 59, 256 * 4));
        // A thin bright line is art, not margin.
        Assert.Null(ArtTrim.FindTileArtStart(Tile(50, 52), 256, 59, 256 * 4));
        // White on the right is where the art is: not a margin.
        Assert.Null(ArtTrim.FindTileArtStart(Tile(150, 200), 256, 59, 256 * 4));
        // A block running past the left part is a light tile.
        Assert.Null(ArtTrim.FindTileArtStart(Tile(10, 200), 256, 59, 256 * 4));
    }

    [Fact]
    public void CleanTileCoversAThinWhiteRightEdge()
    {
        var pixels = Tile(254, 256);

        Assert.True(ArtTrim.CleanTile(pixels, 256, 59, 256 * 4, premultiplied: false));
        var at = ((20 * 256) + 255) * 4;
        Assert.Equal(pixels[at - 8], pixels[at]);
        Assert.True(pixels[at] < 200);
        // Nothing to clean on a plain tile.
        Assert.False(ArtTrim.CleanTile(Tile(0, 0), 256, 59, 256 * 4, premultiplied: false));
    }

    [Fact]
    public void PremultipliedFadeScalesTheColourToo()
    {
        var pixels = Tile(0, 0);
        ArtTrim.FadeTileStart(pixels, 256, 59, 256 * 4, 40, premultiplied: true);
        var at = ((10 * 256) + 48) * 4;
        var original = Tile(0, 0);

        Assert.True(pixels[at + 3] < 255);
        Assert.True(pixels[at] <= pixels[at + 3], "premultiplied colour must not exceed alpha");
        Assert.True(pixels[at] < original[at] || original[at] == 0);
    }

    [Fact]
    public void BadBuffersFindNothing()
    {
        Assert.Null(ArtTrim.FindSquare(new byte[16], 4, 4, 8));
        Assert.Null(ArtTrim.FindSquare(new byte[64], 4, 4, 16 * 2));
        Assert.Null(ArtTrim.FindSquare([], 0, 0, 0));
    }
}
