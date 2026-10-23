using System.IO;
using Hearthpulse.Core;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards, empty states included.</summary>
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

        _main.ShowBattlegroundsTab("Heroes");
    }
}
