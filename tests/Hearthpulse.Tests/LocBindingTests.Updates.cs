using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// Settings → About, updates (TASK-2026-11-09-update-delivery, AC15/AC17): "Check now" disables itself and the
    /// status says "Checking…" until the result; "What's new in 0.15.0" is folded, a click shows the notes and the
    /// GitHub link, which opens the release page; the title follows the language.
    /// </summary>
    [Fact]
    public void About_check_now_and_whats_new_are_bound() => Headless.Run(AboutUpdates);

    private static void AboutUpdates()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1260, Height = 840 };
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-about-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        var opened = new List<Uri>();
        model.OpenBrowser = u =>
        {
            opened.Add(u);
            return true;
        };
        var asked = 0;
        model.UpdateCheckRequested += () => asked++;
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            view.ShowSettingsTab("About");
            Dispatcher.UIThread.RunJobs();

            var check = Named<Button>(view, "UpdateCheckButton");
            var status = Named<TextBlock>(view, "UpdateStatusLine");
            var toggle = Named<Button>(view, "ReleaseNotesToggle");
            var notes = Named<TextBlock>(view, "ReleaseNotesText");
            var page = Named<Button>(view, "ReleasePageLink");
            Assert.True(check.IsEffectivelyVisible);
            Assert.Equal("Check now", check.Content);
            Assert.Equal(Loc.T("Main.Update.NotChecked"), status.Text);
            Assert.False(toggle.IsEffectivelyVisible);

            Click(check);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, asked);
            Assert.False(check.IsEnabled);
            Assert.Equal("Checking for updates…", status.Text);

            model.UpdateCheckFinished(() => "Version 0.15.0 is available.");
            model.SetReleaseNotes("0.15.0", "Check now button.", new Uri("https://github.com/Zulut30/Hearthpulse_App/releases/tag/v0.15.0"));
            Dispatcher.UIThread.RunJobs();
            Assert.True(check.IsEnabled);
            Assert.Equal("Version 0.15.0 is available.", status.Text);
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.False(notes.IsEffectivelyVisible);
            Assert.Contains(TextsIn(toggle), t => t == "What's new in 0.15.0");
            // A screen reader hears the state from the button's name; the chevron glyph is not read.
            Assert.Equal("What's new in 0.15.0, collapsed", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.Equal(Avalonia.Automation.AccessibilityView.Raw,
                Avalonia.Automation.AutomationProperties.GetAccessibilityView(Named<TextBlock>(view, "ReleaseNotesGlyph")));

            Click(toggle);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("What's new in 0.15.0, expanded", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.True(notes.IsEffectivelyVisible);
            Assert.Equal("Check now button.", notes.Text);
            Assert.True(page.IsEffectivelyVisible);
            Assert.Contains(TextsIn(page), t => t == "All changes on GitHub");

            Click(page);
            Assert.Single(opened);

            Loc.Instance.SetLanguage(Loc.Russian);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(TextsIn(toggle), t => t == "Что нового в 0.15.0");
            Assert.Equal("Проверить сейчас", check.Content);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>What a click does: the button's bound command (raising Click alone skips it).</summary>
    private static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled);
        button.Command!.Execute(button.CommandParameter);
    }

    private static IEnumerable<string?> TextsIn(Control control) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(control).OfType<TextBlock>().Select(t => t.Text);
}
