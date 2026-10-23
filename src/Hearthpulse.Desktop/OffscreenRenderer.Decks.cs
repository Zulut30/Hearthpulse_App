using Avalonia.Controls;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the deck page: a merged deck if there is one (else the most played), its chart hover, a matchup filter.</summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderDeckPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        view.ShowPage("Constructed");
        var deck = host.Main.DeckFamiliesShown
            .OrderByDescending(f => f.IsMerged)
            .ThenByDescending(f => f.Games.Count)
            .FirstOrDefault();
        if (deck is null || !host.Main.OpenDeckPage(deck.Key))
        {
            return;
        }

        var page = host.Main.DeckPage;
        main.Height = 4300;
        await Save(main, Path.Combine(folder, "main-deck-full.png"));
        main.Height = 840;
        await Save(main, Path.Combine(folder, "main-deck.png"));
        if (page.Chart.Count > 0)
        {
            view.HoverDeckGame(page.Chart.Count / 2);
            await Save(main, Path.Combine(folder, "main-deck-hover.png"));
            view.HoverDeckGame(-1);
        }

        if (page.Matchups.FirstOrDefault() is { } matchup)
        {
            page.SelectMatchup.Execute(matchup.Key);
            await Save(main, Path.Combine(folder, "main-deck-filtered.png"));
            page.ClearMatchup.Execute(null);
        }

        // Phone width: cards stack, the meta columns and average turn hide.
        main.Width = 420;
        main.Height = 6200;
        await Save(main, Path.Combine(folder, "main-deck-compact.png"));
        main.Width = 1260;
        main.Height = 840;
        page.Back.Execute(null);
    }
}
