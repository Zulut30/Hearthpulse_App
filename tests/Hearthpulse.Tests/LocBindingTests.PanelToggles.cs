using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The live bar's panel buttons and the settings page switch the same setting: a button turns the page's switch
    /// (which binds to AppSettings, which does not notify) and saves once; the page's switch turns the button.
    /// </summary>
    [Fact]
    public void Live_bar_panel_buttons_and_the_settings_page_stay_in_step() => Headless.Run(PanelToggles);

    private static void PanelToggles()
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        var settings = new AppSettings();
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-panel-toggles-{Guid.NewGuid():N}.db")), settings);
        try
        {
            var view = new MainView { DataContext = model };
            // Styles and templates only: the window is not shown, so nothing measures text (the headless run has no fonts).
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            var saves = 0;
            view.SettingsChanged += () => saves++;

            var timer = model.PanelToggles.Single(t => t.Id == nameof(AppSettings.ShowTurnTimer));
            var pageSwitch = view.FindControl<ScrollViewer>("PageSettings")!.GetLogicalDescendants().OfType<CheckBox>()
                .Single(c => Equals(c.Content, Loc.T("Widgets.Turn.Setting")));
            Assert.True(pageSwitch.IsChecked);

            timer.IsOn = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.ShowTurnTimer);
            Assert.False(pageSwitch.IsChecked);
            Assert.Equal(1, saves);

            // The button re-reads the setting when told: by then the page's switch has written it.
            var seen = new List<bool>();
            timer.PropertyChanged += (_, _) => seen.Add(timer.IsOn);
            pageSwitch.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.ShowTurnTimer);
            Assert.True(timer.IsOn);
            Assert.NotEmpty(seen);
            Assert.All(seen, Assert.True);
            Assert.Equal(2, saves);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
