using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The usability pass (TASK-2026-10-29-ux-polish) in the Avalonia views: what was hidden in tips or easy to hit by
/// mistake. Styles, templates and bindings only: the window is not shown, so nothing measures text (no fonts here).
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Busy_line_tip_carries_the_whole_message() => Headless.Run(() => WithMainView((view, model) =>
    {
        model.Busy = Loc.T("Host.Busy.GameNotFound");
        Dispatcher.UIThread.RunJobs();

        var line = view.FindControl<DockPanel>("BusyLine")!;
        Assert.True(line.IsVisible);
        var shown = line.Children.OfType<TextBlock>().Last();
        Assert.Equal(model.Busy, shown.Text);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, shown.TextTrimming);

        var tip = Assert.IsType<StackPanel>(ToolTip.GetTip(line));
        // A tooltip takes its owner's data context when it opens; unopened it is handed over here.
        tip.DataContext ??= model;
        Dispatcher.UIThread.RunJobs();
        var texts = tip.Children.OfType<TextBlock>().ToList();
        Assert.Equal(model.Busy, texts[0].Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, texts[0].TextWrapping);
        Assert.Equal(Loc.T("Main.Busy.Tip"), texts[1].Text);
    }));

    [Fact]
    public void Settings_cards_say_what_they_are_for_and_the_replay_rule_is_a_visible_line() => Headless.Run(() => WithMainView((view, _) =>
    {
        // The same notes, in the same order, as the WPF settings page (UxPolishContractTests); the leaderboard
        // calibration has its cross-platform wording here.
        string[] cards =
        [
            "Settings.Note.Language", "Settings.Note.Theme", "Settings.Note.App", "Settings.Note.Overlay", "Settings.Note.Layout",
            "Settings.Leaderboard.CaptionCrossPlatform", "Settings.Note.Tracker", "Settings.Note.Counters", "Settings.Note.Account",
            "Settings.Note.Data", "Settings.Note.Sources",
        ];
        var notes = view.FindControl<ScrollViewer>("PageSettings")!.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("cardNote")).ToList();
        Assert.Equal(cards.Select(Loc.T), notes.Select(t => t.Text));

        var rule = view.FindControl<TextBlock>("ReplayLimitNote")!;
        Assert.True(rule.IsVisible);
        Assert.Equal(Loc.T("Match.Settings.ReplayLimit"), rule.Text);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, rule.TextWrapping);
        // The data folder path above it no longer hides the rule in its tip.
        var folder = Assert.IsType<TextBlock>(((Panel)rule.Parent!).Children[((Panel)rule.Parent!).Children.IndexOf(rule) - 1]);
        Assert.Null(ToolTip.GetTip(folder));
    }));

    [Fact]
    public void Counter_cross_has_its_own_column_and_leaves_the_art_to_open_the_cards() => Headless.Run(() =>
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var view = new Hearthpulse.UI.Overlay.OverlayView { DataContext = new Hearthpulse.UI.Overlay.OverlayViewModel() };
            var template = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["CounterTemplate"]);
            var row = template.Build(null)!;
            var hide = Assert.Single(row.GetLogicalDescendants().OfType<Button>(), b => b.Classes.Contains("hideCounter"));
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, hide.HorizontalAlignment);
            // The row's own button (art, title, value) keeps an empty last column the cross sits over.
            var grid = row.GetLogicalDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count > 0);
            Assert.Equal(4, grid.ColumnDefinitions.Count);
            Assert.True(grid.ColumnDefinitions[3].Width.Value >= hide.Width);
            Assert.DoesNotContain(grid.Children, c => Grid.GetColumn(c) == 3);
        }
        finally
        {
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>Both counter crosses name the real place the counter comes back from, in each language.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Counter_hide_tips_name_the_tracker_tab_and_the_counters_card(string language)
    {
        var table = Loc.Table(language);
        foreach (var key in new[] { "Overlay.Counter.HideTip", "Overlay.BgCounter.HideTip" })
        {
            Assert.Contains(table["Settings.Tab.Tracker"], table[key], StringComparison.Ordinal);
            Assert.Contains(table["Settings.Counters.Title"], table[key], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Leaderboard_calibration_shows_each_sliders_value() => Headless.Run(() => WithMainView((view, _) =>
    {
        foreach (var name in new[] { "LeaderboardTopSlider", "LeaderboardSlotSlider", "LeaderboardWidthSlider" })
        {
            var slider = view.FindControl<Slider>(name)!;
            slider.Value = 0.08;
            Dispatcher.UIThread.RunJobs();
            var value = ((Grid)slider.Parent!).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2);
            Assert.Equal(0.08.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), value.Text);
        }
    }));

    /// <summary>The main view over a fresh view model with the theme applied, in English.</summary>
    private static void WithMainView(Action<MainView, MainViewModel> body, AppSettings? settings = null)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        var model = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-ux-polish-{Guid.NewGuid():N}.db")), settings ?? new AppSettings());
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            body(view, model);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
