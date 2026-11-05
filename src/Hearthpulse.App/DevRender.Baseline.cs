using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the frames of the visual baselines (render group <c>baseline</c>).</summary>
public partial class App
{
    /// <summary>
    /// Render group <c>baseline</c>: the main-window pages with the made-up games, and the overlay's moments on 16:9, 16:10
    /// and 4:3 screens (<see cref="VisualBaseline"/>), as <c>tools/visual/visual_check.py</c> compares them.
    /// </summary>
    private async Task RenderBaselineAsync(string folder)
    {
        foreach (var game in VisualBaseline.Games)
        {
            _history.Add(game);
        }

        await _mainViewModel.RefreshAsync();
        foreach (var page in VisualBaseline.Pages)
        {
            _main.ShowPage(page);
            await Task.Delay(2500);
            _main.UpdateLayout();
            SaveAtDefaultSize(Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
        }

        foreach (var (moment, snapshot) in VisualBaseline.Moments)
        {
            _overlayViewModel.Update(snapshot(_cards), _cards, _settings, null, null);
            foreach (var (name, width, height) in VisualBaseline.Screens)
            {
                await _overlay.RenderToFileAsync(Path.Combine(folder, $"overlay-{moment}-{name}.png"), width, height, 0);
            }
        }

        _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
    }

    /// <summary>
    /// The main window as it is at its default size (1260 x 840), whatever the screen: Windows keeps a window within the
    /// screen, and the CI runner's is 1024 x 768. Its template is laid out at that size for the frame, over the window's
    /// background.
    /// </summary>
    private void SaveAtDefaultSize(string path)
    {
        const int width = 1260, height = 840;
        // The window's template (its border around the content), as the window draws it.
        var root = (FrameworkElement)VisualTreeHelper.GetChild(_main, 0);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(_main.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
