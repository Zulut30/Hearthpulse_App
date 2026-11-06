using System.IO;
using Hearthpulse.Core;

namespace Hearthpulse.App;

/// <summary>Render-check scenes of the Settings tabs (<c>main-settings-general.png</c>...), in the interface language of the data copy.</summary>
public partial class App
{
    private static readonly string[] SettingsTabs = ["General", "Appearance", "Overlay", "Tracker", "Stream", "Account", "Data", "About"];

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

    /// <summary>
    /// HEARTHPULSE_RENDER_ONLY=settings: the Appearance tab (gallery, accent, background, shape) and the search with
    /// "size" typed in the interface language (<c>main-settings-appearance.png</c>, <c>main-settings-search.png</c>).
    /// </summary>
    private async Task RenderSettingsToolsAsync(string folder)
    {
        _main.ShowPage("Settings");
        _main.ShowSettingsTab("Appearance");
        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, "main-settings-appearance.png"));
        _mainViewModel.SettingsQuery = Core.Localization.Loc.T("Settings.Overlay.Size");
        await Task.Delay(800);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, "main-settings-search.png"));
        _mainViewModel.SettingsQuery = string.Empty;
        _main.ShowSettingsTab("General");
    }
}
