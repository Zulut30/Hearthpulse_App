using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.App.Main;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the My Stats pages: each page at full length and the chart hover cards.</summary>
public partial class App
{
    private async Task RenderMyStatsAsync(string folder)
    {
        foreach (var page in new[] { "Battlegrounds", "Constructed", "Arena" })
        {
            _main.ShowPage(page);
            if (page == "Constructed")
            {
                _main.ShowConstructedTab("Decks");
            }

            await Task.Delay(2500);
            _main.UpdateLayout();
            // The Battlegrounds page goes on into the long tier lists: only its stats are of interest here.
            if (page == "Battlegrounds")
            {
                SaveFull(_main.BgStats, Path.Combine(folder, "main-battlegrounds-full.png"));
            }
            else if (_main.FindName("Page" + page) is ScrollViewer { Content: FrameworkElement content })
            {
                SaveFull(content, Path.Combine(folder, $"main-{page.ToLowerInvariant()}-full.png"));
            }
        }

        // Hover cards: the latest game that saved a final board (Battlegrounds), a ranked game mid-chart (Constructed).
        _main.ShowPage("Battlegrounds");
        var bgPoints = _mainViewModel.BgRatingChart;
        if (bgPoints.Count > 0)
        {
            var index = bgPoints.Select((p, i) => (Point: (BgGamePoint)p, Index: i))
                .Where(x => x.Point.Board.Count > 0)
                .Select(x => x.Index)
                .DefaultIfEmpty(bgPoints.Count - 1)
                .Last();
            _main.BgStats.HoverGame(index);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-battlegrounds-hover.png"));
            _main.BgStats.HoverGame(-1);
        }

        _main.ShowPage("Constructed");
        _main.ShowConstructedTab("Decks");
        var ctPoints = _mainViewModel.CtRankChart;
        if (ctPoints.Count > 0)
        {
            _main.CtStats.HoverGame(ctPoints.Count / 2);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-constructed-hover.png"));
            _main.CtStats.HoverGame(-1);
        }
    }

    /// <summary>Saves a page's whole content (not just what the window shows) on the window background.</summary>
    private static void SaveFull(FrameworkElement content, string path)
    {
        const double pad = 40;
        var width = Math.Ceiling(content.ActualWidth + (2 * pad));
        var height = Math.Ceiling(content.ActualHeight + pad);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(content.TryFindResource("Bg") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(pad, pad / 2, content.ActualWidth, content.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
