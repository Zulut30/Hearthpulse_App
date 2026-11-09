using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Updates;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scenes of the Settings tabs (<c>main-settings-general.png</c>...), in the interface language of the data copy.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>Release notes shaped like Core's plain text (English, as on GitHub), for the About scenes.</summary>
    private const string SampleReleaseNotes =
        "Updates you can trust\n" +
        "- Hearthpulse installs only files signed with the project's key.\n" +
        "- A failed install is rolled back, tried once more, and explained on the next start.\n" +
        "- \"Check now\" and \"What's new\" in Settings, About.\n" +
        "Fixes\n" +
        "- The update check respects GitHub's limit and keeps looking after the first new version.";

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
        await RenderAboutUpdatesAsync(main, view, model, folder);
        view.ShowSettingsTab("General");
    }

    /// <summary>
    /// Settings → About, updates (TASK-2026-11-09-update-delivery): checking, up to date, a downloaded update with
    /// "What's new" folded and unfolded, a failed install (<c>main-settings-about-*.png</c>).
    /// </summary>
    private static async Task RenderAboutUpdatesAsync(Window main, MainView view, MainViewModel model, string folder)
    {
        view.ShowSettingsTab("About");
        static void Asked()
        {
        }

        model.UpdateCheckRequested += Asked;
        model.CheckUpdatesNow.Execute(null);
        model.UpdateCheckRequested -= Asked;
        await SaveAboutAsync(main, folder, "checking");
        model.UpdateCheckFinished(() => Core.Localization.Loc.F("Core.Update.UpToDate", UpdateService.CurrentVersion));
        await SaveAboutAsync(main, folder, "latest");
        model.SetUpdateReady("0.15.0");
        model.SetReleaseNotes("0.15.0", SampleReleaseNotes, new Uri("https://github.com/Zulut30/Hearthpulse_App/releases/tag/v0.15.0"));
        await SaveAboutAsync(main, folder, "notes");
        model.ToggleReleaseNotes.Execute(null);
        await SaveAboutAsync(main, folder, "notes-open");
        var failed = new InstallReport("0.15.0", Succeeded: false, ExitCode: 2, FailedAttempts: 1, RolledBack: true);
        model.SetInstallFailed(failed.Version, failed.Message);
        await SaveAboutAsync(main, folder, "install-failed");
    }

    private static async Task SaveAboutAsync(Window main, string folder, string state)
    {
        Dispatcher.UIThread.RunJobs();
        await Save(main, Path.Combine(folder, $"main-settings-about-{state}.png"));
    }
}
