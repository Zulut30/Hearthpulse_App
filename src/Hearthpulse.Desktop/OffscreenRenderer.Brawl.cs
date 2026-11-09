using Avalonia.Controls;
using Avalonia.VisualTree;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Tavern Brawl page (docs/brawl.md) and the render groups that run alone.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary><c>HEARTHPULSE_RENDER_PAGES</c> as a set of page names, null when not set (the <c>pages</c> group's pick).</summary>
    private static HashSet<string>? RenderPagesOnly() =>
        Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_PAGES") is { Length: > 0 } pages
            ? new HashSet<string>(pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=brawl,history</c>: only those groups, not the whole set (a check of one page stays a
    /// frame or two instead of a hundred). Empty: everything.
    /// </summary>
    private static string[] RenderOnlyGroups =>
        (Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ONLY") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool RenderOnly(string group) => RenderOnlyGroups.Contains(group, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>HEARTHPULSE_RENDER_WIDTH=1040</c>: the main window's width for the groups, else as it is.</summary>
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
    private static async Task RenderOnlyAsync(Window main, MainView view, AppHost host, string folder)
    {
        if (RenderOnly("mystats")) await RenderMyStatsAsync(main, view, host, folder);
        if (RenderOnly("sourcecapture"))
        {
            view.ShowPage("Overview");
            await Save(main, Path.Combine(folder, "main-source-notice.png"));
            view.ShowPage("Settings");
            view.ShowSettingsTab("Data");
            if (view.FindControl<Avalonia.Controls.ScrollViewer>("PageSettings") is { } dataScroll)
                dataScroll.Offset = new Avalonia.Vector(0, 360);
            await Save(main, Path.Combine(folder, "main-source-data.png"));
        }
        if (RenderOnly("brawl"))
        {
            await RenderBrawlPageAsync(main, view, host, folder);
        }

        if (RenderOnly("arenapage"))
        {
            await RenderArenaPageAsync(main, view, host, folder);
        }

        if (RenderOnly("settings"))
        {
            await RenderSettingsToolsAsync(main, view, folder);
        }

        if (RenderOnly("share"))
        {
            await RenderShareAsync(host, folder);
        }

        if (RenderOnly("home"))
        {
            // The whole home page: the recent games and the deck banners sit below the fold, so a tall frame.
            view.ShowPage("Overview");
            var height = main.Height;
            main.Height = 2400;
            await Save(main, Path.Combine(folder, "main-overview-full.png"));
            main.Height = height;
        }

        if (RenderOnly("baseline"))
        {
            await RenderBaselineAsync(main, view, host, folder);
        }

        if (RenderOnly("odds"))
        {
            // The combat odds and the Battlegrounds bar (OffscreenRenderer.CombatOdds.cs).
            var (overlay, overlayView) = OpenOverlay(host, host.Settings);
            await RenderCombatOddsAsync(folder, host, host.Settings, overlay, overlayView);
            overlay.Close();
        }

        if (RenderOnly("account"))
        {
            // The account center (Settings → Hearthpulse account) and the connection light, signed in as the in-memory
            // sample "Render" (no network, no real token); HEARTHPULSE_RENDER_ACCOUNT=connected|offline|ended.
            var state = RenderAccountState;
            await host.Main.ShowRenderAccountAsync(state);
            if (RenderWidth is { } accountWidth)
            {
                main.Width = accountWidth;
                main.Height = accountWidth < 600 ? 1800 : main.Height;
            }

            view.ShowPage("Settings");
            view.ShowSettingsTab("Account");
            await Save(main, Path.Combine(folder, $"main-settings-account-{state}.png"));
        }

        if (RenderOnly("pages"))
        {
            // Home and Battlegrounds as the whole window at HEARTHPULSE_RENDER_WIDTH (1080 high from 1600 wide): the page
            // hero and its art at a wide window; HEARTHPULSE_RENDER_PAGES=Arena,Brawl picks other sidebar pages.
            var width = RenderWidth ?? 1260;
            main.Width = width;
            main.Height = width >= 1600 ? 1080 : 840;
            foreach (var page in RenderPagesOnly() is { } pages ? MainViewModel.NavPages.Where(pages.Contains) : ["Overview", "Battlegrounds"])
            {
                view.ShowPage(page);
                await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}-{width:0}.png"));
            }
        }

        if (RenderOnly("setup"))
        {
            // The setup wizard (D4) on each step, over Home; Start closes it (in the data copy) for the groups after it.
            view.ShowPage("Overview");
            for (var step = 0; step < MainViewModel.SetupStepKeys.Count; step++)
            {
                host.Main.ShowSetup(step);
                await Task.Delay(1500);
                await Save(main, Path.Combine(folder, $"main-setup-{step + 1}.png"));
            }

            // The logs step with the logs off: a log.config that does not exist (read, never written here)
            host.Main.SetupLogConfigFile = Path.Combine(folder, "absent", "log.config");
            host.Main.ShowSetup(0);
            await Save(main, Path.Combine(folder, "main-setup-1-off.png"));
            host.Main.SetupLogConfigFile = null;

            host.Main.CloseSetup.Execute(null);
        }

        if (RenderOnly("steppng") && host.History.Recent(400).FirstOrDefault(g => g.Replay is not null && g.IsBattlegrounds) is { } stepGame
            && await host.Main.OpenMatchPageAsync(stepGame.Key))
        {
            // "Save the step as PNG" (plan R4): the header with its camera button, then the picture the button writes.
            view.ShowPage("History");
            host.Main.MatchPage.Frame = host.Main.MatchPage.FrameMax / 2;
            await Save(main, Path.Combine(folder, "main-match-header.png"));
            if (view.GetVisualDescendants().OfType<Border>().FirstOrDefault(c => c.Name == "BattlegroundsCard") is { } card)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, "main-match-step-export-bg.png"), Hearthpulse.UI.Main.Match.MatchView.Encode(card));
            }

            host.Main.MatchPage.Back.Execute(null);
        }

        if (RenderOnly("history") || RenderOnly("historylist"))
        {
            if (RenderWidth is { } width)
            {
                main.Width = width;
            }

            view.ShowPage("History");
            await Save(main, Path.Combine(folder, "main-history.png"));
            if (RenderOnly("history") && host.History.Recent(400).FirstOrDefault(g => g.Replay is not null && g.IsBattlegrounds) is { } battlegrounds
                && await host.Main.OpenMatchPageAsync(battlegrounds.Key))
            {
                view.ShowPage("History");
                // The first step (the hero choice) on the Battlegrounds table card: below the fold, so a tall frame.
                var height = main.Height;
                main.Height = 2400;
                await Save(main, Path.Combine(folder, "main-match-bg-full.png"));
                if (view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "BattlegroundsStepCard" && c.IsEffectivelyVisible) is { } table)
                {
                    SaveCrop(main, (table.Parent as Control) ?? table, Path.Combine(folder, "main-match-bg-table.png"));
                    File.Delete(Path.Combine(folder, "main-match-bg-full.png"));
                }

                main.Height = height;
                host.Main.MatchPage.Back.Execute(null);
            }
        }
    }

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
