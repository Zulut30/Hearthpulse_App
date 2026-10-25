using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App;

/// <summary>
/// Render-check scenes of the match page: the newest Constructed game with a replay (a step two thirds in, from both
/// sides, its decks and analysis) and the newest Battlegrounds game with one.
/// </summary>
public partial class App
{
    private async Task RenderMatchPageAsync(string folder)
    {
        var games = _history.Recent(400).Where(g => g.Replay is not null).ToList();
        var constructed = games.FirstOrDefault(g => !g.IsBattlegrounds);
        if (constructed is not null && await _mainViewModel.OpenMatchPageAsync(constructed.Key))
        {
            var page = _mainViewModel.MatchPage;
            page.Frame = page.FrameMax * 2 / 3;
            await SaveMatchAsync(folder, "main-match");
            page.BottomOpponent = true;
            await SaveMatchAsync(folder, "main-match-opponent-side", full: false);
            page.BottomLocal = true;
            page.ShowDecks = true;
            await SaveMatchAsync(folder, "main-match-decks");
            page.ShowAnalysis = true;
            await SaveMatchAsync(folder, "main-match-analysis");
            page.ShowReplay = true;

            // The step's choice row: both mulligans (the opponent's as the log revealed it), then a Discover.
            foreach (var (kind, name) in new[] { (Core.Replay.ReplayActionKind.Mulligan, "main-match-mulligan"), (Core.Replay.ReplayActionKind.Choice, "main-match-choice") })
            {
                if (page.FrameOf(kind) is >= 0 and var frame)
                {
                    page.Frame = frame;
                    await SaveMatchAsync(folder, name);
                }
            }
        }


        var battlegrounds = games.FirstOrDefault(g => g.IsBattlegrounds);
        if (battlegrounds is not null && await _mainViewModel.OpenMatchPageAsync(battlegrounds.Key))
        {
            await SaveMatchAsync(folder, "main-match-bg");
        }

        _mainViewModel.MatchPage.Back.Execute(null);
    }

    private async Task SaveMatchAsync(string folder, string name, bool full = true)
    {
        _main.ShowPage("History");
        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, name + ".png"));
        if (full && _main.FindName("PageMatch") is ScrollViewer { Content: FrameworkElement content })
        {
            SaveFull(content, Path.Combine(folder, name + "-full.png"));
        }
    }
}
