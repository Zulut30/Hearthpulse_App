using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds.Site;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards (every tavern tier and the spells), empty states included.</summary>
public partial class App
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=bgtabs</c>: the tier tabs on the site's lists as the data folder's cache has them
    /// (or one live load): every tab, a sorted list, Duos, a slice on demand, two tavern tiers, the empty Spells,
    /// numbers marked as out of date and "unavailable". Files <c>main-bg-*.png</c>.
    /// </summary>
    private async Task RenderBgTabsAsync(string folder)
    {
        var stats = await LoadBgSiteAsync();
        _main.ShowPage("Battlegrounds");
        await Task.Delay(1500);

        async Task Shot(string name, string tab, Func<Task>? prepare = null)
        {
            try
            {
                _main.ShowBattlegroundsTab(tab);
                if (prepare is not null)
                {
                    await prepare();
                }

                await Task.Delay(1500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-bg-{name}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the {name} tier tab failed", ex);
            }
        }

        await Shot("heroes", "Heroes");

        // A slice that is still loading: the rows, the heads and the picker stand where they stood in the frame before,
        // and the line says what loads in place of the date.
        var slices = _mainViewModel.BgSiteSlices;
        var never = new TaskCompletionSource<Hearthpulse.Core.Battlegrounds.Site.BgSiteTierList?>();
        _mainViewModel.BgSiteSlices = new Hearthpulse.App.Main.BgSiteSliceLoader((_, _) => never.Task, _ => never.Task);
        await Shot("heroes-loading", "Heroes", () => { _mainViewModel.SetBracket.Execute(5); return Task.CompletedTask; });
        _mainViewModel.BgSiteSlices = slices;
        _mainViewModel.SetBracket.Execute(50);

        await Shot("heroes-sorted", "Heroes", () => { _mainViewModel.SetHeroSort.Execute("Pick"); return Task.CompletedTask; });
        _mainViewModel.SetHeroSort.Execute("Pick");
        _mainViewModel.SetHeroSort.Execute("Pick");
        await Shot("heroes-top20", "Heroes", () => { _mainViewModel.SetBracket.Execute(20); return Task.Delay(3000); });
        await Shot("heroes-duos", "Heroes", () => { _mainViewModel.SetBracket.Execute(50); ShowBgMode("Duos"); return Task.Delay(1000); });
        ShowBgMode("Battlegrounds");
        await Shot("comps", "Comps");
        await Shot("trinkets", "Trinkets");
        await Shot("trinkets-top50", "Trinkets", () => { _mainViewModel.SetBracket.Execute(50); return Task.Delay(3000); });
        _mainViewModel.SetBracket.Execute(1);
        await Shot("cards-t1", "Cards", () => { ShowCardTier(1); return Task.CompletedTask; });
        await Shot("cards-t4", "Cards", () => { ShowCardTier(4); return Task.CompletedTask; });
        await Shot("cards-spells", "Cards", () => { ShowCardTier(0); return Task.CompletedTask; });
        ShowCardTier(1);

        if (stats is not null)
        {
            // The same lists as a cache file a month old would give them: the line says so.
            var old = DateTime.UtcNow.AddDays(-30);
            _mainViewModel.SetBgSiteStats(stats with
            {
                Heroes = stats.Heroes is null ? null : stats.Heroes with { FetchedAt = old },
                Trinkets = stats.Trinkets is null ? null : stats.Trinkets with { FetchedAt = old },
                CompsFetchedAt = stats.CompsFetchedAt is null ? null : old,
            });
            await Shot("heroes-stale", "Heroes");
            await Shot("comps-stale", "Comps");
        }

        // No network and no cache.
        _mainViewModel.SetBgSiteStats(null);
        await Shot("heroes-unavailable", "Heroes");
        await Shot("cards-unavailable", "Cards");
        _mainViewModel.SetBgSiteStats(stats);
        _main.ShowBattlegroundsTab("Heroes");
    }

    private async Task RenderBattlegroundsTiersAsync(string folder)
    {
        // The site's lists load beside the rest (LoadMetaAsync does not wait for them): the frames wait here.
        await LoadBgSiteAsync();
        _main.ShowPage("Battlegrounds");
        await Task.Delay(1500);
        foreach (var tab in new[] { "Heroes", "Comps", "Trinkets", "Cards" })
        {
            try
            {
                _main.ShowBattlegroundsTab(tab);
                await Task.Delay(1500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-battlegrounds-{tab.ToLowerInvariant()}.png"));
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
                ShowCardTier(tier);
                await Task.Delay(1500);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-battlegrounds-cards-{(tier == 0 ? "spells" : "t" + tier.ToString(CultureInfo.InvariantCulture))}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the card list, tier {tier}, failed", ex);
            }
        }

        ShowCardTier(1);
        _main.ShowBattlegroundsTab("Heroes");
    }

    /// <summary>Picks "Solo" ("Battlegrounds") or "Duos" as a click on its segment would: the segment and the lists.</summary>
    private void ShowBgMode(string mode)
    {
        foreach (var segment in LogicalDescendants<RadioButton>(_main).Where(r => r.GroupName == "BgMode"))
        {
            segment.IsChecked = (segment.CommandParameter as string) == mode;
        }

        _mainViewModel.SetBgMode.Execute(mode);
    }

    /// <summary>Picks a tavern tier of the card list (0 = spells) as a click on its segment would: the segment and the list.</summary>
    private void ShowCardTier(int tier)
    {
        var parameter = tier.ToString(CultureInfo.InvariantCulture);
        foreach (var segment in LogicalDescendants<RadioButton>(_main).Where(r => r.GroupName == "CardTier"))
        {
            segment.IsChecked = (segment.CommandParameter as string) == parameter;
        }

        _mainViewModel.SetCardTier.Execute(parameter);
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in LogicalDescendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
