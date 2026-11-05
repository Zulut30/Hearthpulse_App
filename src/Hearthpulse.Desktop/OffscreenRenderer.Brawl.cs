using Avalonia.Controls;
using Avalonia.VisualTree;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Tavern Brawl page (docs/brawl.md) and the render groups that run alone.</summary>
internal static partial class OffscreenRenderer
{
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
            System.Globalization.CultureInfo.InvariantCulture, out var width) && width >= 800 ? width : null;

    /// <summary>The groups of <see cref="RenderOnlyGroups"/>: <c>brawl</c> (the Brawl page), <c>history</c> (History and a Battlegrounds match).</summary>
    private static async Task RenderOnlyAsync(Window main, MainView view, AppHost host, string folder)
    {
        if (RenderOnly("brawl"))
        {
            await RenderBrawlPageAsync(main, view, host, folder);
        }

        if (RenderOnly("account"))
        {
            // The account center (Settings → Hearthpulse account), as the data copy has it (no saved sign-in: signed out).
            view.ShowPage("Settings");
            view.ShowSettingsTab("Account");
            await Save(main, Path.Combine(folder, "main-settings-account.png"));
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
