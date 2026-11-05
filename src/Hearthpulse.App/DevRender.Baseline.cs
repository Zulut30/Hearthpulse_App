using System.IO;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the frames of the visual baselines (render group <c>baseline</c>).</summary>
public partial class App
{
    /// <summary>
    /// Render group <c>baseline</c>: the main-window pages and the overlay's layout mode on 16:9, 16:10 and 4:3 screens
    /// (<see cref="VisualBaseline"/>), as <c>tools/visual/visual_check.py</c> compares them.
    /// </summary>
    private async Task RenderBaselineAsync(string folder)
    {
        foreach (var page in VisualBaseline.Pages)
        {
            _main.ShowPage(page);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
        }

        _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
        _overlayViewModel.LayoutEditing = true;
        foreach (var (name, width, height) in VisualBaseline.Screens)
        {
            await _overlay.RenderToFileAsync(Path.Combine(folder, $"overlay-layout-{name}.png"), width, height, 0);
        }

        _overlayViewModel.LayoutEditing = false;
    }
}
