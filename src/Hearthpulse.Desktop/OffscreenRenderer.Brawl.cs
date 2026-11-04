using Avalonia.Controls;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Tavern Brawl page (docs/brawl.md): full width and compact.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=brawl</c>: only the Brawl page, not the whole set (a check of one page stays two frames
    /// instead of a hundred).
    /// </summary>
    private static bool RenderOnlyBrawl =>
        string.Equals(Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ONLY"), "brawl", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>main-brawl.png</c> and <c>main-brawl-compact.png</c>: this week's classes and the data copy's Brawl games.</summary>
    private static async Task RenderBrawlPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        await host.LoadBrawlMetaAsync();
        view.ShowPage("Brawl");
        // The tables are built off the UI thread once the numbers are in.
        await Task.Delay(1000);
        await Save(main, Path.Combine(folder, "main-brawl.png"));

        var (width, height) = (main.Width, main.Height);
        main.Width = 420;
        main.Height = 1800;
        await Task.Delay(300);
        await Save(main, Path.Combine(folder, "main-brawl-compact.png"));
        main.Width = width;
        main.Height = height;
    }
}
