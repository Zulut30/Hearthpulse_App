using System.IO;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the Tavern Brawl page (docs/brawl.md).</summary>
public partial class App
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=brawl</c>: only the Brawl page, not the whole set (a check of one page stays a frame or
    /// two instead of a hundred).
    /// </summary>
    private static bool RenderOnlyBrawl =>
        string.Equals(Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ONLY"), "brawl", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>main-brawl.png</c>: this week's classes (the API or its cache) and the data copy's Brawl games.</summary>
    private async Task RenderBrawlAsync(string folder)
    {
        await LoadBrawlMetaAsync();
        _main.ShowPage("Brawl");
        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, "main-brawl.png"));
    }
}
