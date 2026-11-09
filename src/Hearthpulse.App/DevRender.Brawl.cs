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

    /// <summary>The groups of <see cref="RenderOnlyGroups"/>: <c>brawl</c> (the Brawl page), <c>home</c> (the whole home page), <c>pages</c> (Home and Battlegrounds at a width), <c>setup</c> (the setup wizard on each step), <c>history</c> (History and a Battlegrounds match).</summary>
    private async Task RenderOnlyAsync(string folder)
    {
        if (RenderOnly("sourcecapture"))
        {
            _main.ShowPage("Overview");
            await Task.Delay(300);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-source-notice.png"));
            _main.ShowPage("Settings");
            _main.ShowSettingsTab("Data");
            await Task.Delay(300);
            Named<System.Windows.Controls.ScrollViewer>(_main, "PageSettings")?.ScrollToVerticalOffset(360);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-source-data.png"));
        }
        if (RenderOnly("brawl"))
        {
            await RenderBrawlAsync(folder);
        }

        if (RenderOnly("arenapage"))
        {
            await RenderArenaPageAsync(folder);
        }

        if (RenderOnly("share"))
        {
            await RenderShareAsync(folder);
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

        if (RenderOnly("settings"))
        {
            await RenderSettingsToolsAsync(folder);
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

        if (RenderOnly("odds"))
        {
            // The combat odds and the Battlegrounds bar (DevRender.CombatOdds.cs).
            await RenderCombatOddsAsync(folder);
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

        if (RenderOnly("pages"))
        {
            // Home and Battlegrounds as the whole window at HEARTHPULSE_RENDER_WIDTH (1080 high from 1600 wide): the page
            // hero and its art at a wide window; HEARTHPULSE_RENDER_PAGES=Arena,Brawl picks other sidebar pages.
            var width = RenderWidth ?? 1260;
            _main.Width = width;
            _main.Height = width >= 1600 ? 1080 : 840;
            foreach (var page in RenderPagesOnly() is { } pages ? Hearthpulse.App.Main.MainViewModel.NavPages.Where(pages.Contains) : ["Overview", "Battlegrounds"])
            {
                _main.ShowPage(page);
                await Task.Delay(2500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}-{width:0}.png"));
            }
        }

        if (RenderOnly("setup"))
        {
            // The setup wizard (D4) on each step, over Home; Start closes it (in the data copy) for the groups after it.
            _main.ShowPage("Overview");
            for (var step = 0; step < Hearthpulse.App.Main.MainViewModel.SetupStepKeys.Count; step++)
            {
                _mainViewModel.ShowSetup(step);
                await Task.Delay(1500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-setup-{step + 1}.png"));
            }

            // The logs step with the logs off: a log.config that does not exist (read, never written here)
            _mainViewModel.SetupLogConfigFile = Path.Combine(folder, "absent", "log.config");
            _mainViewModel.ShowSetup(0);
            await Task.Delay(500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-setup-1-off.png"));
            _mainViewModel.SetupLogConfigFile = null;

            _mainViewModel.CloseSetup.Execute(null);
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
