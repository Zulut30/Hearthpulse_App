using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards (every tavern tier and the spells), empty states included.</summary>
public partial class App
{
    private async Task RenderBattlegroundsTiersAsync(string folder)
    {
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
