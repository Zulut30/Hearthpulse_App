using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF portrait trim moves the square's rows to the front of its pooled pixel buffer instead of copying them into a
/// second one: the result must be exactly the square cut from the source, also when the pooled buffer is reused.
/// </summary>
public sealed class CardArtTrimTests
{
    private const int Side = 200;

    [Fact]
    public void Trimmed_portrait_is_exactly_the_square_of_the_source_and_a_reused_buffer_changes_nothing()
    {
        var trim = typeof(CardArt).GetMethod("TrimMargins", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var (x, y, width, height) in new[] { (30, 4, 140, 192), (0, 20, Side, 160), (11, 7, 170, 181) })
        {
            var source = Sheet(x, y, width, height);
            var pixels = Pixels(source);
            var crop = ArtTrim.FindSquare(pixels, Side, Side, Side * 4)!.Value;
            var expected = new byte[crop.Size * crop.Size * 4];
            source.CopyPixels(new Int32Rect(crop.X, crop.Y, crop.Size, crop.Size), expected, crop.Size * 4, 0);

            var trimmed = (BitmapSource)trim.Invoke(null, [source])!;

            Assert.Equal(crop.Size, trimmed.PixelWidth);
            Assert.Equal(crop.Size, trimmed.PixelHeight);
            Assert.Equal(expected, Pixels(trimmed));
        }
    }

    /// <summary>White paper with a patterned illustration in the given rectangle, as a frozen BGRA bitmap.</summary>
    private static BitmapSource Sheet(int artX, int artY, int artWidth, int artHeight)
    {
        var pixels = new byte[Side * Side * 4];
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var art = x >= artX && x < artX + artWidth && y >= artY && y < artY + artHeight;
                var at = ((y * Side) + x) * 4;
                pixels[at] = art ? (byte)((x * 7) % 120) : (byte)255;
                pixels[at + 1] = art ? (byte)((y * 5) % 120) : (byte)255;
                pixels[at + 2] = art ? (byte)((x + y) % 120) : (byte)255;
                pixels[at + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(Side, Side, 96, 96, PixelFormats.Bgra32, null, pixels, Side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
