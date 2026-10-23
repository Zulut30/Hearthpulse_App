using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Settings tabs (<c>main-settings-general.png</c>...), in the interface language of the data copy.</summary>
internal static partial class OffscreenRenderer
{
    private static readonly string[] SettingsTabs = ["General", "Overlay", "Tracker", "Account", "Data", "About"];

    private static async Task RenderSettingsTabsAsync(Window main, MainView view, string folder)
    {
        view.ShowPage("Settings");
        foreach (var tab in SettingsTabs)
        {
            try
            {
                view.ShowSettingsTab(tab);
                Dispatcher.UIThread.RunJobs();
                await Save(main, Path.Combine(folder, $"main-settings-{tab.ToLowerInvariant()}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the {tab} settings tab failed", ex);
            }
        }

        view.ShowSettingsTab("General");
    }
}
