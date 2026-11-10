using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards (every tavern tier and the spells), empty states included.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=bgtabs</c>: the tier tabs on the site's lists as the data folder's cache has them
    /// (or one live load): every tab, a sorted list, Duos, a slice on demand, two tavern tiers, the empty Spells,
    /// numbers marked as out of date and "unavailable". Files <c>main-bg-*.png</c>.
    /// </summary>
    private static async Task RenderBgTabsAsync(Window main, MainView view, AppHost host, string folder)
    {
        var stats = await host.LoadBgSiteAsync();
        var model = host.Main;
        view.ShowPage("Battlegrounds");

        async Task Shot(string name, string tab, Func<Task>? prepare = null)
        {
            try
            {
                view.ShowBattlegroundsTab(tab);
                if (prepare is not null)
                {
                    await prepare();
                }

                // A tab shown for the first time is laid out on the next pass: scroll again once it has a size.
                await Task.Delay(300);
                Dispatcher.UIThread.RunJobs();
                view.ShowBattlegroundsTab(tab);
                await Save(main, Path.Combine(folder, $"main-bg-{name}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the {name} tier tab failed", ex);
            }
        }

        await Shot("heroes", "Heroes");

        // A slice that is still loading: the rows, the heads and the picker stand where they stood in the frame before,
        // and the line says what loads in place of the date.
        var slices = model.BgSiteSlices;
        var never = new TaskCompletionSource<Hearthpulse.Core.Battlegrounds.Site.BgSiteTierList?>();
        model.BgSiteSlices = new BgSiteSliceLoader((_, _) => never.Task, _ => never.Task);
        await Shot("heroes-loading", "Heroes", () => { model.SetBracket.Execute(5); return Task.CompletedTask; });
        model.BgSiteSlices = slices;
        model.SetBracket.Execute(50);

        // By name: the heroes stand in another order inside their tiers than the service's, not only under an arrow.
        await Shot("heroes-sorted", "Heroes", () => { model.SetHeroSort.Execute("Name"); return Task.CompletedTask; });
        model.SetHeroSort.Execute("Name");
        model.SetHeroSort.Execute("Name");
        await Shot("heroes-top20", "Heroes", () => { model.SetBracket.Execute(20); return Task.Delay(3000); });
        await Shot("heroes-duos", "Heroes", () => { model.SetBracket.Execute(50); ShowBgMode(view, "Duos"); return Task.Delay(1000); });
        ShowBgMode(view, "Battlegrounds");
        await Shot("comps", "Comps");
        await Shot("trinkets", "Trinkets");
        await Shot("trinkets-top50", "Trinkets", () => { model.SetBracket.Execute(50); return Task.Delay(3000); });
        model.SetBracket.Execute(1);
        await Shot("cards-t1", "Cards", () => { ShowCardTier(view, 1); return Task.CompletedTask; });
        await Shot("cards-t4", "Cards", () => { ShowCardTier(view, 4); return Task.CompletedTask; });
        await Shot("cards-spells", "Cards", () => { ShowCardTier(view, 0); return Task.CompletedTask; });
        ShowCardTier(view, 1);

        // The compact layout at its narrowest: the names keep their room, the secondary columns go.
        var (width, height) = (main.Width, main.Height);
        main.Width = 420;
        main.Height = 1200;
        await Shot("heroes-compact", "Heroes");
        await Shot("trinkets-compact", "Trinkets");
        await Shot("cards-compact", "Cards");
        await Shot("comps-compact", "Comps");
        main.Width = width;
        main.Height = height;

        if (stats is not null)
        {
            // The same lists as a cache file a month old would give them: the line says so.
            var old = DateTime.UtcNow.AddDays(-30);
            model.SetBgSiteStats(stats with
            {
                Heroes = stats.Heroes is null ? null : stats.Heroes with { FetchedAt = old },
                Trinkets = stats.Trinkets is null ? null : stats.Trinkets with { FetchedAt = old },
                CompsFetchedAt = stats.CompsFetchedAt is null ? null : old,
            });
            await Shot("heroes-stale", "Heroes");
            await Shot("comps-stale", "Comps");
        }

        // No network and no cache.
        model.SetBgSiteStats(null);
        await Shot("heroes-unavailable", "Heroes");
        await Shot("cards-unavailable", "Cards");
        model.SetBgSiteStats(stats);
        view.ShowBattlegroundsTab("Heroes");
    }

    private static async Task RenderBattlegroundsTiersAsync(Window main, MainView view, string folder)
    {
        // The site's lists load beside the rest (LoadMetaAsync does not wait for them): the frames wait here, as long
        // as a request may take.
        for (var i = 0; i < 100 && (view.DataContext as MainViewModel)?.HeroTab.EmptyText == Loc.T("Main.Bg.CommunityLoading"); i++)
        {
            await Task.Delay(250);
        }

        view.ShowPage("Battlegrounds");
        foreach (var tab in new[] { "Heroes", "Comps", "Trinkets", "Cards" })
        {
            try
            {
                view.ShowBattlegroundsTab(tab);
                // A tab shown for the first time is laid out on the next pass: scroll again once it has a size.
                await Task.Delay(300);
                Dispatcher.UIThread.RunJobs();
                view.ShowBattlegroundsTab(tab);
                await Save(main, Path.Combine(folder, $"main-battlegrounds-{tab.ToLowerInvariant()}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the {tab} tier list failed", ex);
            }
        }

        // The card list on every tavern tier and on the spells (the Cards tab is still open): the art frame of each row.
        foreach (var tier in new[] { 1, 2, 3, 4, 5, 6, 7, 0 })
        {
            try
            {
                ShowCardTier(view, tier);
                // Save waits for the rows' art itself.
                Dispatcher.UIThread.RunJobs();
                await Save(main, Path.Combine(folder, $"main-battlegrounds-cards-{(tier == 0 ? "spells" : "t" + tier.ToString(CultureInfo.InvariantCulture))}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the card list, tier {tier}, failed", ex);
            }
        }

        ShowCardTier(view, 1);
        view.ShowBattlegroundsTab("Heroes");
    }

    /// <summary>Picks "Solo" ("Battlegrounds") or "Duos" as a click on its segment would: the segment and the lists.</summary>
    private static void ShowBgMode(MainView view, string mode)
    {
        foreach (var segment in view.GetLogicalDescendants().OfType<RadioButton>().Where(r => r.GroupName == "BgMode"))
        {
            segment.IsChecked = (segment.CommandParameter as string) == mode;
        }

        (view.DataContext as MainViewModel)?.SetBgMode.Execute(mode);
    }

    /// <summary>Picks a tavern tier of the card list (0 = spells) as a click on its segment would: the segment and the list.</summary>
    private static void ShowCardTier(MainView view, int tier)
    {
        var parameter = tier.ToString(CultureInfo.InvariantCulture);
        foreach (var segment in view.GetLogicalDescendants().OfType<RadioButton>().Where(r => r.GroupName == "CardTier"))
        {
            segment.IsChecked = (segment.CommandParameter as string) == parameter;
        }

        (view.DataContext as MainViewModel)?.SetCardTier.Execute(parameter);
    }
}
