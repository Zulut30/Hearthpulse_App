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
                SaveFull(_main.PageBattlegrounds.BgStats, Path.Combine(folder, "main-battlegrounds-full.png"));
            }
            // A page control (Main/Pages) holds its ScrollViewer; the Arena page is still one itself.
            else if (_main.FindName("Page" + page) is { } found
                     && (found as ScrollViewer ?? (found as UserControl)?.Content as ScrollViewer) is { Content: FrameworkElement content })
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
            _main.PageBattlegrounds.BgStats.HoverGame(index);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-battlegrounds-hover.png"));

            // The same card with a late game's board: five-digit stats and a 40-letter hero name, which must not run together.
            var chart = _main.PageBattlegrounds.BgStats.RatingChart;
            var wide = bgPoints.ToList();
            wide[index] = WideBoardSample.Apply((BgGamePoint)wide[index]);
            chart.SetCurrentValue(Controls.StatsLineChart.PointsProperty, wide);
            _main.PageBattlegrounds.BgStats.HoverGame(index);
            await Task.Delay(1500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-battlegrounds-hover-wide.png"));
            chart.SetCurrentValue(Controls.StatsLineChart.PointsProperty, bgPoints);
            _main.PageBattlegrounds.BgStats.HoverGame(-1);
        }

        _main.ShowPage("Constructed");
        _main.ShowConstructedTab("Decks");
        var ctPoints = _mainViewModel.CtRankChart;
        if (ctPoints.Count > 0)
        {
            _main.PageConstructed.CtStats.HoverGame(ctPoints.Count / 2);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-constructed-hover.png"));
            _main.PageConstructed.CtStats.HoverGame(-1);
        }
    }

    /// <summary>Saves a page's whole content (not just what the window shows) on the window background.</summary>
    private static void SaveFull(FrameworkElement content, string path)
    {
        if (content.Parent is ScrollViewer page)
        {
            Controls.PageRowsPanel.BuildAllRows(page);
        }

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

/// <summary>A late game's final board for the rating chart's hover card: stats from three to five digits, a long hero name.</summary>
internal static class WideBoardSample
{
    public static BgGamePoint Apply(BgGamePoint point) => point with
    {
        HeroName = Hearthpulse.Core.Localization.Loc.Instance.Language == Hearthpulse.Core.Localization.Loc.Russian
            ? "Ноздорму на курорте в бухте Злачной Тени"
            : "Nozdormu on vacation at the Gilded Cove!",
        Board = [.. new[] { (186, 195), (373, 138), (955, 955), (1240, 1300), (12_500, 9800), (9999, 10_000), (99_960, 7) }
            .Select((s, i) => new BoardMinionRow(
                point.Board.Count > 0 ? point.Board[i % point.Board.Count].CardId : "BG21_001",
                point.Board.Count > 0 ? point.Board[i % point.Board.Count].Name : "Minion",
                s.Item1, s.Item2, Golden: i % 3 == 0))],
    };
}
