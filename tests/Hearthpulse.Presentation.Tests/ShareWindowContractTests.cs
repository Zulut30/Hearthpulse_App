using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Decks;
using Hearthpulse.App.Main.Share;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF share window (TASK-2026-11-07-growth-app): every picture comes out at twice its size and opaque (the
/// clipboard's bitmap keeps no transparency), the deck layouts switch, and the 16:9 cover is 1920 × 1080.
/// </summary>
public sealed class ShareWindowContractTests
{
    [Fact]
    public void Wpf_share_pictures_are_opaque_at_twice_their_size()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The share picture check failed", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Frost", 0, 0, 1)));
        var settings = new AppSettings();

        var recap = new SessionRecapCard("November 6", [new RecapSection("Ranked · Standard", "HERO_04", [new ShareStat("Games", "6", "83%", 1)], [new SharePlace("1", true)])], "empty");
        Picture(new ShareViewModel(ShareKind.SessionRecap, recap, "Session recap", "file", settings, "Streamer#1"), 640).Close();

        var row = new DeckListRow("CARD", "Card", 1, "LEGENDARY", "★");
        var deck = new ShareViewModel(ShareKind.Deck, new DeckPictureCard("Deck", "Mage", "HERO_08", "30 cards", "CODE", [row], [row], [], [new ManaCurveBar("1", 1, 1)]), "Deck", "file", settings, null);
        var window = Picture(deck, 420);
        deck.Cover = true;
        window.UpdateLayout();
        Assert.IsType<DeckCoverView>(window.Card);
        var cover = Decode(window.Encode());
        Assert.Equal((1920, 1080), (cover.PixelWidth, cover.PixelHeight));
        window.Close();

        var year = new YearCard("Hearthpulse 2026 in review", "sub", [new ShareStat("Games this year", "73")], "HERO_11", string.Empty, string.Empty, "empty");
        Picture(new ShareViewModel(ShareKind.Year, year, "Year", "file", settings, null), 720).Close();
        Assert.Equal("a_b_c", ShareWindow.SafeFileName("a/b:c"));
    }

    /// <summary>Opens the window off screen and checks its picture: twice the card's width, the corners opaque.</summary>
    private static ShareWindow Picture(ShareViewModel model, double width)
    {
        var window = new ShareWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        var png = Decode(window.Encode());
        Assert.Equal((int)(width * 2), png.PixelWidth);
        var pixels = new byte[4];
        foreach (var (x, y) in new[] { (0, 0), (png.PixelWidth - 1, 0), (0, png.PixelHeight - 1), (png.PixelWidth - 1, png.PixelHeight - 1) })
        {
            png.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
            Assert.True(pixels[3] == 255, $"{model.Kind}: pixel ({x}, {y}) of {png.PixelWidth} x {png.PixelHeight} has alpha {pixels[3]}");
        }
        return window;
    }

    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
    }
}
