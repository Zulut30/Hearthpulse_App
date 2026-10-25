using Avalonia.Controls;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>
/// Render-check scenes of the match page: the newest Constructed game with a replay (a step two thirds in, from both
/// sides, its decks and analysis, and at phone width) and the newest Battlegrounds game with one.
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderMatchPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        var games = host.History.Recent(400).Where(g => g.Replay is not null).ToList();
        var page = host.Main.MatchPage;
        if (games.FirstOrDefault(g => !g.IsBattlegrounds) is { } constructed && await host.Main.OpenMatchPageAsync(constructed.Key))
        {
            view.ShowPage("History");
            page.Frame = page.FrameMax * 2 / 3;
            await SaveMatch(main, folder, "main-match");
            page.BottomOpponent = true;
            await Save(main, Path.Combine(folder, "main-match-opponent-side.png"));
            page.BottomLocal = true;
            page.ShowDecks = true;
            await SaveMatch(main, folder, "main-match-decks");
            page.ShowAnalysis = true;
            await SaveMatch(main, folder, "main-match-analysis");
            page.ShowReplay = true;

            // The step's choice row: both mulligans (the opponent's as the log revealed it), then a Discover.
            foreach (var (kind, name) in new[] { (Core.Replay.ReplayActionKind.Mulligan, "main-match-mulligan"), (Core.Replay.ReplayActionKind.Choice, "main-match-choice") })
            {
                if (page.FrameOf(kind) is >= 0 and var frame)
                {
                    page.Frame = frame;
                    await Save(main, Path.Combine(folder, name + ".png"));
                }
            }

            page.Frame = page.FrameMax * 2 / 3;

            // Phone width: the turn's actions go under the table.
            main.Width = 420;
            main.Height = 2600;
            await Save(main, Path.Combine(folder, "main-match-compact.png"));
            main.Width = 1260;
            main.Height = 840;
        }

        if (games.FirstOrDefault(g => g.IsBattlegrounds) is { } battlegrounds && await host.Main.OpenMatchPageAsync(battlegrounds.Key))
        {
            view.ShowPage("History");
            await SaveMatch(main, folder, "main-match-bg");
        }

        page.Back.Execute(null);
    }

    private static async Task SaveMatch(Window main, string folder, string name)
    {
        await Save(main, Path.Combine(folder, name + ".png"));
        main.Height = 2400;
        await Save(main, Path.Combine(folder, name + "-full.png"));
        main.Height = 840;
    }
}
