using Avalonia.Controls;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the frames of the visual baselines (render group <c>baseline</c>).</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// Render group <c>baseline</c>: the main-window pages and the overlay's layout mode on 16:9, 16:10 and 4:3 screens
    /// (<see cref="VisualBaseline"/>), as <c>tools/visual/visual_check.py</c> compares them.
    /// </summary>
    private static async Task RenderBaselineAsync(Window main, MainView view, AppHost host, string folder)
    {
        foreach (var page in VisualBaseline.Pages)
        {
            view.ShowPage(page);
            await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
        }

        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, host.Settings, null, null);
        host.Overlay.LayoutEditing = true;
        foreach (var (name, width, height) in VisualBaseline.Screens)
        {
            var (overlay, _) = OpenOverlay(host, host.Settings, width, height);
            await Save(overlay, Path.Combine(folder, $"overlay-layout-{name}.png"));
            overlay.Close();
        }

        host.Overlay.LayoutEditing = false;
    }
}
