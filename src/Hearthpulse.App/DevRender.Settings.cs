using System.IO;
using Hearthpulse.Core;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the Settings tabs (<c>main-settings-general.png</c>...), in the interface language of the data copy.</summary>
public partial class App
{
    private static readonly string[] SettingsTabs = ["General", "Overlay", "Tracker", "Account", "Data", "About"];

    private async Task RenderSettingsTabsAsync(string folder)
    {
        _main.ShowPage("Settings");
        foreach (var tab in SettingsTabs)
        {
            try
            {
                _main.ShowSettingsTab(tab);
                await Task.Delay(800);
                _main.UpdateLayout();
                Save(_main, Path.Combine(folder, $"main-settings-{tab.ToLowerInvariant()}.png"));
            }
            catch (IOException ex)
            {
                Diag.Error($"Render of the {tab} settings tab failed", ex);
            }
        }

        _main.ShowSettingsTab("General");
    }
}
