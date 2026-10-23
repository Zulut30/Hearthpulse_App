using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Battlegrounds tier lists: heroes, comps, trinkets and cards, empty states included.</summary>
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

        view.ShowBattlegroundsTab("Heroes");
    }
}
