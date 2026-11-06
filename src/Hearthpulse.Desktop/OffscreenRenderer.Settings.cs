using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Settings tabs (<c>main-settings-general.png</c>...), in the interface language of the data copy.</summary>
internal static partial class OffscreenRenderer
{
    private static readonly string[] SettingsTabs = ["General", "Appearance", "Overlay", "Tracker", "Stream", "Account", "Data", "About"];

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

    /// <summary>
    /// HEARTHPULSE_RENDER_ONLY=settings: the Appearance tab (gallery, accent, background, shape) and the search with
    /// "size" typed in the interface language (<c>main-settings-appearance.png</c>, <c>main-settings-search.png</c>).
    /// </summary>
    private static async Task RenderSettingsToolsAsync(Window main, MainView view, string folder)
    {
        if (view.DataContext is not MainViewModel model)
        {
            return;
        }

        view.ShowPage("Settings");
        view.ShowSettingsTab("Appearance");
        await Task.Delay(2500);
        Dispatcher.UIThread.RunJobs();
        await Save(main, Path.Combine(folder, "main-settings-appearance.png"));
        model.SettingsQuery = Core.Localization.Loc.T("Settings.Overlay.Size");
        Dispatcher.UIThread.RunJobs();
        await Save(main, Path.Combine(folder, "main-settings-search.png"));
        model.SettingsQuery = string.Empty;
        view.ShowSettingsTab("General");
    }
}
