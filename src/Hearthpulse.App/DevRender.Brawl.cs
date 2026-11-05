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
            System.Globalization.CultureInfo.InvariantCulture, out var width) && width >= 400 ? width : null;

    /// <summary><c>HEARTHPULSE_RENDER_ACCOUNT</c>: the sample account's state in the <c>account</c> group (connected, offline, ended).</summary>
    private static string RenderAccountState =>
        Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ACCOUNT")?.Trim().ToLowerInvariant() switch
        {
            "offline" => "offline",
            "ended" => "ended",
            _ => "connected",
        };

    /// <summary>The groups of <see cref="RenderOnlyGroups"/>: <c>brawl</c> (the Brawl page), <c>home</c> (the whole home page), <c>history</c> (History and a Battlegrounds match).</summary>
    private async Task RenderOnlyAsync(string folder)
    {
        if (RenderOnly("brawl"))
        {
            await RenderBrawlAsync(folder);
        }

        if (RenderOnly("home"))
        {
            // The whole home page: the recent games and the deck banners sit below the fold.
            _main.ShowPage("Overview");
            await Task.Delay(2500);
            _main.UpdateLayout();
            if (_main.PageOverview.Content is System.Windows.Controls.ScrollViewer { Content: System.Windows.FrameworkElement content })
            {
                SaveFull(content, Path.Combine(folder, "main-overview-full.png"));
            }
        }

        if (RenderOnly("overlay"))
        {
            await RenderOverlayCheckAsync(folder);
        }

        if (RenderOnly("overlay-opacity"))
        {
            await RenderOpacityCheckAsync(folder);
        }

        if (RenderOnly("baseline"))
        {
            await RenderBaselineAsync(folder);
        }

        if (RenderOnly("account"))
        {
            // The account center (Settings → Hearthpulse account) and the connection light, signed in as the in-memory
            // sample "Render" (no network, no real token); HEARTHPULSE_RENDER_ACCOUNT=connected|offline|ended.
            var state = RenderAccountState;
            await _mainViewModel.ShowRenderAccountAsync(state);
            if (RenderWidth is { } accountWidth)
            {
                _main.Width = accountWidth;
            }

            _main.ShowPage("Settings");
            _main.ShowSettingsTab("Account");
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, $"main-settings-account-{state}.png"));
        }

        if (RenderOnly("ctmatch") || RenderOnly("steppng"))
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

            if (RenderOnlyGroups.Contains("steppng", StringComparer.OrdinalIgnoreCase) && Named<System.Windows.Controls.Border>(_main, "TableCard") is { } exported)
            {
                // "Save the step as PNG" (plan R4): the picture the header's camera button writes.
                await File.WriteAllBytesAsync(Path.Combine(folder, "main-match-step-export.png"), Hearthpulse.App.Main.Match.MatchView.Encode(exported));
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
