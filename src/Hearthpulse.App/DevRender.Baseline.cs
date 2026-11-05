using System.IO;
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
            Save(_main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
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
}
