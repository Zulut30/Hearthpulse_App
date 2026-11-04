using System.IO;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the Tavern Brawl page (docs/brawl.md) and the render groups that run alone.</summary>
public partial class App
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=brawl,history</c>: only those groups, not the whole set (a check of one page stays a
    /// frame or two instead of a hundred). Empty: everything.
    /// </summary>
    private static string[] RenderOnlyGroups =>
        (Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ONLY") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool RenderOnly(string group) => RenderOnlyGroups.Contains(group, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>HEARTHPULSE_RENDER_WIDTH=1040</c>: the main window's width for the groups (the minimum is 1040), else as it is.</summary>
    private static double? RenderWidth =>
        double.TryParse(Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_WIDTH"), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var width) && width >= 800 ? width : null;

    /// <summary>The groups of <see cref="RenderOnlyGroups"/>: <c>brawl</c> (the Brawl page), <c>history</c> (History and a Battlegrounds match).</summary>
    private async Task RenderOnlyAsync(string folder)
    {
        if (RenderOnly("brawl"))
        {
            await RenderBrawlAsync(folder);
        }

        if (RenderOnly("ctmatch"))
        {
            await RenderConstructedTableOnlyAsync(folder);
        }

        if (RenderOnly("history") || RenderOnly("historylist"))
        {
            if (RenderWidth is { } width)
            {
                _main.Width = width;
            }

            _main.ShowPage("History");
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-history.png"));
            if (RenderOnly("history") && _history.Recent(400).FirstOrDefault(g => g.Replay is not null && g.IsBattlegrounds) is { } battlegrounds
                && await _mainViewModel.OpenMatchPageAsync(battlegrounds.Key))
            {
                // The first step (the hero choice) on the Battlegrounds table card: below the fold of the page.
                await Task.Delay(2500);
                _main.UpdateLayout();
                if (Named<System.Windows.Controls.Border>(_main, "BattlegroundsStepCard") is { } table)
                {
                    SaveCard(table, Path.Combine(folder, "main-match-bg-table.png"));
                }

                _mainViewModel.MatchPage.Back.Execute(null);
            }
        }
    }

    /// <summary><c>ctmatch</c>: the newest Constructed replay's table card two thirds in (the hero lines' health and armor).</summary>
    private async Task RenderConstructedTableOnlyAsync(string folder)
    {
        if (_history.Recent(400).FirstOrDefault(g => g.Replay is not null && !g.IsBattlegrounds) is { } game
            && await _mainViewModel.OpenMatchPageAsync(game.Key))
        {
            var page = _mainViewModel.MatchPage;
            page.Frame = page.FrameMax * 2 / 3;
            await Task.Delay(2500);
            _main.UpdateLayout();
            if (Named<System.Windows.Controls.Border>(_main, "TableCard") is { } table)
            {
                SaveCard(table, Path.Combine(folder, "main-match-table.png"));
            }

            page.Back.Execute(null);
        }
    }

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
