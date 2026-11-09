using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Settings → About, updates (TASK-2026-11-09-update-delivery, AC15–AC19): "Check now", its refusal, "What's new",
/// a failed install and a downloaded update that later results do not hide, in both apps' view models.
/// </summary>
public sealed class UpdateAboutContractTests
{
    private static readonly Uri Page = new("https://github.com/Zulut30/Hearthpulse_App/releases/tag/v0.15.0");

    private static dynamic Model(bool wpf)
    {
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        return wpf ? new WpfMain(history, new AppSettings(), TestApp.Data) : new AvaMain(history, new AppSettings(), TestApp.Data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Check_now_shows_checking_and_disables_the_button_until_the_result(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        var model = Model(wpf);
        var asked = 0;
        model.UpdateCheckRequested += new Action(() => asked++);
        Assert.True((bool)model.UpdateCheckSupported);
        Assert.True((bool)model.CanCheckUpdates);

        model.CheckUpdatesNow.Execute(null);

        Assert.Equal(1, asked);
        Assert.True((bool)model.UpdateChecking);
        Assert.False((bool)model.CanCheckUpdates);
        Assert.Equal("Проверяю обновления…", (string)model.UpdateStatus);

        model.CheckUpdatesNow.Execute(null);
        Assert.Equal(1, asked);

        model.UpdateCheckFinished(new Func<string>(() => "Hearthpulse 0.15.0 — последняя версия."));

        Assert.False((bool)model.UpdateChecking);
        Assert.True((bool)model.CanCheckUpdates);
        Assert.Equal("Hearthpulse 0.15.0 — последняя версия.", (string)model.UpdateStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_refused_check_shows_the_reason_and_gives_the_button_back(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf);
        var reasonCalls = 0;
        var reason = "GitHub's limit is reached; try again after 14:05.";
        // The host's handler: the loop refused synchronously, nothing was sent. The trigger's text is live: its next
        // answer changes it, so the view model keeps the text of the moment of the refusal.
        model.UpdateCheckRequested += new Action(() => model.UpdateCheckRefused(new Func<string>(() =>
        {
            reasonCalls++;
            return reason;
        })));

        model.CheckUpdatesNow.Execute(null);

        Assert.False((bool)model.UpdateChecking);
        Assert.True((bool)model.CanCheckUpdates);
        Assert.Equal("GitHub's limit is reached; try again after 14:05.", (string)model.UpdateStatus);
        reason = string.Empty;
        Assert.Equal("GitHub's limit is reached; try again after 14:05.", (string)model.UpdateStatus);
        Assert.Equal(1, reasonCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failure_without_a_check_result_ends_checking_and_gives_the_button_back(bool wpf)
    {
        // Review M2: a round that threw reports only Failed; "Checking…" must not stay.
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf);
        model.UpdateCheckRequested += new Action(() => { });
        model.CheckUpdatesNow.Execute(null);
        Assert.True((bool)model.UpdateChecking);

        model.SetUpdateStatus(new Func<string>(() => "Could not check for updates: locked."));

        Assert.False((bool)model.UpdateChecking);
        Assert.True((bool)model.CanCheckUpdates);
        Assert.Equal("Could not check for updates: locked.", (string)model.UpdateStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_whats_new_link_names_its_state_for_a_screen_reader(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        var model = Model(wpf);
        Assert.Equal(string.Empty, (string)model.ReleaseNotesToggleName);
        model.SetReleaseNotes("0.15.0", "Notes.", Page);

        Assert.Equal("Что нового в 0.15.0, свёрнуто", (string)model.ReleaseNotesToggleName);
        model.ToggleReleaseNotes.Execute(null);
        Assert.Equal("Что нового в 0.15.0, развёрнуто", (string)model.ReleaseNotesToggleName);

        Loc.Instance.SetLanguage(Loc.English);
        Assert.Equal("What's new in 0.15.0, expanded", (string)model.ReleaseNotesToggleName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_a_host_listening_the_button_does_not_stick_in_checking(bool wpf)
    {
        var model = Model(wpf);

        model.CheckUpdatesNow.Execute(null);

        Assert.False((bool)model.UpdateChecking);
        Assert.True((bool)model.CanCheckUpdates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Whats_new_is_folded_unfolds_on_click_and_opens_the_release_page(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        var model = Model(wpf);
        var opened = new List<Uri>();
        model.OpenBrowser = new Func<Uri, bool>(u =>
        {
            opened.Add(u);
            return true;
        });
        Assert.False((bool)model.HasReleaseNotes);

        model.SetReleaseNotes("0.15.0", "Update check now has a button.\nRelease notes in About.", Page);

        Assert.True((bool)model.HasReleaseNotes);
        Assert.Equal("Что нового в 0.15.0", (string)model.ReleaseNotesTitle);
        Assert.False((bool)model.ReleaseNotesExpanded);
        Assert.False((bool)model.ReleaseNotesShown);
        Assert.False((bool)model.ReleasePageShown);

        model.ToggleReleaseNotes.Execute(null);

        Assert.True((bool)model.ReleaseNotesShown);
        Assert.True((bool)model.ReleasePageShown);
        Assert.Equal("Update check now has a button.\nRelease notes in About.", (string)model.ReleaseNotesText);
        model.OpenReleasePage.Execute(null);
        Assert.Equal([Page], opened);

        Loc.Instance.SetLanguage(Loc.English);
        Assert.Equal("What's new in 0.15.0", (string)model.ReleaseNotesTitle);
        Assert.Equal("All changes on GitHub", Loc.T("Settings.About.AllChanges"));

        model.ToggleReleaseNotes.Execute(null);
        Assert.False((bool)model.ReleaseNotesShown);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_notes_hide_the_block_and_no_page_hides_the_link(bool wpf)
    {
        var model = Model(wpf);

        model.SetReleaseNotes("0.15.0", null, Page);
        Assert.False((bool)model.HasReleaseNotes);
        model.ToggleReleaseNotes.Execute(null);
        Assert.False((bool)model.ReleaseNotesShown);
        model.ToggleReleaseNotes.Execute(null);

        model.SetReleaseNotes("0.15.0", "Notes.", null);
        Assert.True((bool)model.HasReleaseNotes);
        Assert.False((bool)model.HasReleasePage);
        model.ToggleReleaseNotes.Execute(null);
        Assert.True((bool)model.ReleaseNotesShown);
        Assert.False((bool)model.ReleasePageShown);

        // A newer version folds the notes again.
        model.SetReleaseNotes("0.15.1", "Newer notes.", Page);
        Assert.False((bool)model.ReleaseNotesExpanded);
        Assert.Contains("0.15.1", (string)model.ReleaseNotesTitle, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_downloaded_update_stays_in_the_status_and_a_newer_one_replaces_it(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf);
        var installs = 0;
        model.UpdateRequested += new Action(() => installs++);

        model.SetUpdateReady("0.15.0");
        var downloaded = Loc.F("Main.Update.Downloaded", "0.15.0");
        Assert.Equal(downloaded, (string)model.UpdateStatus);

        model.SetUpdateStatus(new Func<string>(() => "Could not reach GitHub."));
        Assert.Equal(downloaded, (string)model.UpdateStatus);
        model.UpdateCheckFinished(new Func<string>(() => "Hearthpulse 0.15.0 is the latest version."));
        Assert.Equal(downloaded, (string)model.UpdateStatus);

        model.SetUpdateReady("0.15.1");
        Assert.True((bool)model.UpdateReady);
        Assert.Equal(Loc.F("Main.Update.Downloaded", "0.15.1"), (string)model.UpdateStatus);
        Assert.Equal((string)model.UpdateStatus, (string)model.UpdateText);

        model.InstallUpdate.Execute(null);
        Assert.Equal(1, installs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failed_install_shows_in_the_status_until_a_newer_version_is_downloaded(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf);
        const string failed = "Version 0.15.0 did not install (code 2); the previous version was restored.";

        model.SetInstallFailed("0.15.0", new Func<string>(() => failed));
        Assert.Equal(failed, (string)model.UpdateStatus);

        model.UpdateCheckFinished(new Func<string>(() => "Version 0.15.0 is available."));
        Assert.Equal(failed, (string)model.UpdateStatus);

        // The same version downloaded again for the retry: the failure is still the news.
        model.SetUpdateReady("0.15.0");
        Assert.Equal(failed, (string)model.UpdateStatus);

        model.SetUpdateReady("0.15.1");
        Assert.Equal(Loc.F("Main.Update.Downloaded", "0.15.1"), (string)model.UpdateStatus);
    }
}
