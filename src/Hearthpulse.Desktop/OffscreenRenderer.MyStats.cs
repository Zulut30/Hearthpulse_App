using Avalonia.Controls;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the My Stats pages: each page in a tall window and the chart hover cards.</summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderMyStatsAsync(Window main, MainView view, AppHost host, string folder)
    {
        // A tall window shows a page's whole stats; the Battlegrounds page goes on into its tier lists below them.
        foreach (var (page, height) in new[] { ("Battlegrounds", 1900), ("Constructed", 2000), ("Arena", 1300) })
        {
            main.Height = height;
            view.ShowPage(page);
            await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}-full.png"));
        }

        main.Height = 840;
        var bgPoints = host.Main.BgRatingChart;
        if (bgPoints.Count > 0)
        {
            view.ShowPage("Battlegrounds");
            var index = bgPoints.Select((p, i) => (Point: (BgGamePoint)p, Index: i))
                .Where(x => x.Point.Board.Count > 0)
                .Select(x => x.Index)
                .DefaultIfEmpty(bgPoints.Count - 1)
                .Last();
            view.HoverBattlegroundsGame(index);
            await Save(main, Path.Combine(folder, "main-battlegrounds-hover.png"));
            view.HoverBattlegroundsGame(-1);
        }

        var ctPoints = host.Main.CtRankChart;
        if (ctPoints.Count > 0)
        {
            view.ShowPage("Constructed");
            view.HoverConstructedGame(ctPoints.Count / 2);
            await Save(main, Path.Combine(folder, "main-constructed-hover.png"));
            view.HoverConstructedGame(-1);
        }

        // Phone-sized pages: filters under the titles, stacked cards, the hero table without its charts.
        main.Width = 420;
        foreach (var (page, height) in new[] { ("Battlegrounds", 2600), ("Constructed", 3000), ("Arena", 2200) })
        {
            main.Height = height;
            view.ShowPage(page);
            await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}-compact.png"));
        }

        main.Width = 1260;
        main.Height = 840;
    }
}
