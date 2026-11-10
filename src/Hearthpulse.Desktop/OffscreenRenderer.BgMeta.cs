using System.Globalization;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards (every tavern tier and the spells), empty states included.</summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderBattlegroundsTiersAsync(Window main, MainView view, string folder)
    {
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
