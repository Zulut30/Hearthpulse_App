namespace Hearthpulse.Core.Cards;

/// <summary>A square piece of an image, in pixels.</summary>
public readonly record struct ArtCrop(int X, int Y, int Size);

/// <summary>
/// Finds the art inside the white paper margins much of HearthstoneJSON's square art comes with (most newer sets:
/// the illustration sits in the middle of a white 256x256 sheet). Shown round or square with UniformToFill, those
/// margins draw a light ring around the portrait.
/// </summary>
public static class ArtTrim
{
    /// <summary>Every colour channel at least this counts as margin white; JPEG noise keeps paper from being exactly 255.</summary>
    private const int LightLevel = 236;

    /// <summary>An edge row or column is margin when this share of its pixels is white (the rest is JPEG noise).</summary>
    private const double LightShare = 0.96;

    /// <summary>Pixels also cut on each trimmed side: JPEG blurs the border between the margin and the art.</summary>
    private const int Seam = 2;

    /// <summary>
    /// The largest square centred on the art inside the white margins, or null when the image has no margins or is
    /// light all over (then the margins cannot be told from the art and nothing is cut).
    /// </summary>
    /// <param name="pixels">32-bit pixels with the colour in the first three bytes (BGRA or RGBA).</param>
    public static ArtCrop? FindSquare(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < (stride * (height - 1)) + (width * 4))
        {
            return null;
        }

        var top = 0;
        while (top < height && RowLight(pixels, stride, top, width))
        {
            top++;
        }

        var bottom = 0;
        while (bottom < height - top && RowLight(pixels, stride, height - 1 - bottom, width))
        {
            bottom++;
        }

        // Columns are judged on the rows left between the top and bottom margins.
        var left = 0;
        while (left < width && ColumnLight(pixels, stride, left, top, height - bottom))
        {
            left++;
        }

        var right = 0;
        while (right < width - left && ColumnLight(pixels, stride, width - 1 - right, top, height - bottom))
        {
            right++;
        }

        if (top + bottom + left + right == 0)
        {
            return null;
        }

        top = top > 0 ? top + Seam : 0;
        bottom = bottom > 0 ? bottom + Seam : 0;
        left = left > 0 ? left + Seam : 0;
        right = right > 0 ? right + Seam : 0;
        var artWidth = width - left - right;
        var artHeight = height - top - bottom;
        if (artWidth * 2 < width || artHeight * 2 < height)
        {
            return null;
        }

        var size = Math.Min(artWidth, artHeight);
        return new ArtCrop(left + ((artWidth - size) / 2), top + ((artHeight - size) / 2), size);
    }

    /// <summary>
    /// Deck-list tiles are cut from the same art, so many carry a full-height white block on their left part, which
    /// shows as a light stripe between the card name and the art. Returns the column where the art starts after the
    /// last such block (only blocks in the left part count: the art itself sits on the right), or null.
    /// </summary>
    public static int? FindTileArtStart(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < (stride * (height - 1)) + (width * 4))
        {
            return null;
        }

        int? start = null;
        var run = 0;
        for (var x = 0; x < width * TileMarginShare; x++)
        {
            if (ColumnLight(pixels, stride, x, 0, height))
            {
                run++;
                continue;
            }

            if (run >= MinTileBlock)
            {
                start = x + Seam;
            }

            run = 0;
        }

        // A block still running at the limit is a light tile, not a margin: it is left alone.
        return run >= MinTileBlock ? null : start;
    }

    /// <summary>
    /// Makes everything left of <paramref name="artStart"/> transparent and fades the art in over a few pixels, so the
    /// row's own background shows instead of the white block.
    /// </summary>
    /// <param name="premultiplied">True when the colour channels are premultiplied by alpha (they are scaled too).</param>
    public static void FadeTileStart(Span<byte> pixels, int width, int height, int stride, int artStart, bool premultiplied)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < Math.Min(width, artStart + TileRamp); x++)
            {
                var at = (y * stride) + (x * 4);
                var keep = x < artStart ? 0 : (x - artStart + 1) / (double)(TileRamp + 1);
                for (var c = premultiplied || keep == 0 ? 0 : 3; c < 4; c++)
                {
                    pixels[at + c] = (byte)Math.Round(pixels[at + c] * keep);
                }
            }
        }
    }

    /// <summary>
    /// Cleans a deck tile in place: fades out a white block on its left part and covers a thin white line on its right
    /// edge with the art next to it. Returns false when the tile had neither and is unchanged.
    /// </summary>
    public static bool CleanTile(Span<byte> pixels, int width, int height, int stride, bool premultiplied)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || pixels.Length < (stride * (height - 1)) + (width * 4))
        {
            return false;
        }

        var changed = false;
        if (FindTileArtStart(pixels, width, height, stride) is { } start)
        {
            FadeTileStart(pixels, width, height, stride, start, premultiplied);
            changed = true;
        }

        var edge = 0;
        while (edge <= MaxTileEdge && ColumnLight(pixels, stride, width - 1 - edge, 0, height))
        {
            edge++;
        }

        if (edge is > 0 and <= MaxTileEdge)
        {
            // One more column than the line itself: JPEG-era seams leave it half light.
            var fill = edge + 1;
            var source = (width - 1 - fill) * 4;
            for (var y = 0; y < height; y++)
            {
                for (var x = width - fill; x < width; x++)
                {
                    pixels.Slice((y * stride) + source, 4).CopyTo(pixels[((y * stride) + (x * 4))..]);
                }
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>A light line on a tile's right edge up to this wide is a seam of the art's white margin.</summary>
    private const int MaxTileEdge = 4;

    /// <summary>White blocks in tiles end before this share of the width (about a third on real tiles).</summary>
    private const double TileMarginShare = 0.45;

    /// <summary>Narrower light runs are bright art (a sword's edge, a spell flash), not margin.</summary>
    private const int MinTileBlock = 3;

    /// <summary>Pixels over which the art fades in after the cut.</summary>
    private const int TileRamp = 16;

    private static bool RowLight(ReadOnlySpan<byte> pixels, int stride, int y, int width)
    {
        var light = 0;
        for (var x = 0; x < width; x++)
        {
            light += IsLight(pixels, (y * stride) + (x * 4)) ? 1 : 0;
        }

        return light >= width * LightShare;
    }

    private static bool ColumnLight(ReadOnlySpan<byte> pixels, int stride, int x, int fromY, int toY)
    {
        var light = 0;
        for (var y = fromY; y < toY; y++)
        {
            light += IsLight(pixels, (y * stride) + (x * 4)) ? 1 : 0;
        }

        return toY > fromY && light >= (toY - fromY) * LightShare;
    }

    private static bool IsLight(ReadOnlySpan<byte> pixels, int at) =>
        pixels[at] >= LightLevel && pixels[at + 1] >= LightLevel && pixels[at + 2] >= LightLevel;
}
