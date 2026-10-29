using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
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

    [Fact]
    public void Overlay_and_tracker_tabs_link_to_each_other_and_an_off_overlay_dims_its_panel_switches() => Headless.Run(() => WithMainView((view, model) =>
    {
        view.ShowSettingsTab("Overlay");
        view.FindControl<Button>("OpenTrackerTab")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindControl<StackPanel>("SettingsTracker")!.IsVisible);
        Assert.False(view.FindControl<StackPanel>("SettingsOverlay")!.IsVisible);
        Assert.Equal("Tracker", model.Settings.SettingsTab);
        view.FindControl<Button>("OpenOverlayTab")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.FindControl<StackPanel>("SettingsOverlay")!.IsVisible);
        Assert.Equal("Overlay", model.Settings.SettingsTab);

        var master = view.FindControl<CheckBox>("OverlaySetting")!;
        var note = view.FindControl<TextBlock>("OverlayOffNote")!;
        var groups = new[] { view.FindControl<StackPanel>("OverlayPanelSwitches")!, view.FindControl<StackPanel>("TrackerPanelSwitches")! };
        Assert.False(note.IsVisible);
        Assert.All(groups, g => Assert.Equal(1, g.Opacity));

        master.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(note.IsVisible);
        // Dimmed, not disabled: panels can still be set up before the overlay is turned back on.
        Assert.All(groups, g => Assert.True(g.Opacity < 1));
        Assert.All(groups.SelectMany(g => g.GetLogicalDescendants().OfType<CheckBox>()), c => Assert.True(c.IsEnabled));

        master.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(note.IsVisible);
        Assert.All(groups, g => Assert.Equal(1, g.Opacity));

        // The live bar's panel buttons follow the live bar's own overlay switch.
        var quick = view.FindControl<ItemsControl>("QuickPanels")!;
        view.FindControl<CheckBox>("OverlaySwitch")!.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(quick.Opacity < 1);
        Assert.True(quick.IsEnabled);
        view.FindControl<CheckBox>("OverlaySwitch")!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, quick.Opacity);
    }));

    [Fact]
    public void Settings_list_the_shortcuts_this_platform_has_and_the_live_bar_names_them() => Headless.Run(() => WithMainView((view, _) =>
    {
        var global = OperatingSystem.IsWindows();
        var card = view.FindControl<Border>("ShortcutsCard")!;
        Assert.Same(view.FindControl<StackPanel>("SettingsGeneral"), card.Parent);
        var rows = card.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("settingLabel")).ToList();
        Assert.Equal(new[] { "Pages", "Back", "Hide", "Layout" }.Select(r => Loc.T("Settings.Shortcuts." + r)), rows.Select(r => r.Text));
        // Ctrl+Shift+H and Ctrl+Shift+L are registered on Windows only; there is no hard refresh in this app.
        Assert.Equal(global, view.FindControl<Grid>("ShortcutHideRow")!.IsVisible);
        Assert.Equal(global, view.FindControl<Grid>("ShortcutLayoutRow")!.IsVisible);
        Assert.DoesNotContain(card.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Settings.Shortcuts.RefreshKeys"));

        Assert.Equal(Loc.T(global ? "Main.Live.ArrangeTip" : "Main.Live.ArrangeTipShort"), ToolTip.GetTip(view.FindControl<Button>("ArrangeButton")!));
        Assert.Equal(Loc.T(global ? "Main.Live.OverlayTip" : "Main.Live.OverlayTipShort"), ToolTip.GetTip(view.FindControl<CheckBox>("OverlaySwitch")!));
    }));

    /// <summary>The overlay switch's tips no longer present Ctrl+Shift+H as its own shortcut; the tray names Ctrl+Shift+R.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Shortcut_texts_say_what_each_combination_does(string language)
    {
        var table = Loc.Table(language);
        Assert.Contains("Ctrl+Shift+R", table["Host.Tray.HardRefresh"], StringComparison.Ordinal);
        // The old wording made Ctrl+Shift+H the switch's own shortcut; now it says the key only hides the overlay for a while.
        var forAWhile = language == "en"
            ? new Dictionary<string, string> { ["Main.Live.OverlayTip"] = "for a while", ["Settings.Overlay.EnabledTip"] = "for a while", ["Settings.Overlay.EnabledTipWindows"] = "for a while" }
            : new Dictionary<string, string> { ["Main.Live.OverlayTip"] = "временно", ["Settings.Overlay.EnabledTip"] = "на время", ["Settings.Overlay.EnabledTipWindows"] = "на время" };
        foreach (var (key, phrase) in forAWhile)
        {
            Assert.Contains("Ctrl+Shift+H", table[key], StringComparison.Ordinal);
            Assert.Contains(phrase, table[key], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Getting_started_steps_follow_the_app_and_its_buttons_run_the_existing_actions()
    {
        var model = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-ux-polish-{Guid.NewGuid():N}.db")), new AppSettings());
        await model.RefreshAsync();
        Assert.True(model.ShowGettingStarted);
        Headless.Run(() => WithMainView((view, _) =>
        {
            var card = view.FindControl<Border>("GettingStartedCard")!;
            Assert.True(card.IsVisible);
            var steps = card.GetLogicalDescendants().OfType<Grid>().ToList();
            Assert.Equal(3, steps.Count);
            Assert.Equal(new[] { "Step1", "Step2", "Step3" }.Select(k => Loc.T("Main.Home.Start." + k)),
                steps.Select(g => g.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 1).Text));

            model.Apply(LiveSnapshot.Empty with { LogConfigMissing = ["Power"] });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([Waiting, Waiting, Waiting], steps.Select(Glyph));

            model.Apply(LiveSnapshot.Empty with { HearthstoneRunning = true });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([Done, Done, Waiting], steps.Select(Glyph));

            var imports = 0;
            var arranges = 0;
            view.ReimportRequested += () => imports++;
            view.ArrangeOverlayRequested += () => arranges++;
            view.FindControl<Button>("GettingStartedImport")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            view.FindControl<Button>("GettingStartedArrange")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((1, 1), (imports, arranges));
        }, model: model));

        static string? Glyph(Grid step) => step.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 0 && t.IsVisible).Text;
    }

    private const string Done = "\U000F05E0";
    private const string Waiting = "\U000F0766";

    [Fact]
    public void Live_line_tip_carries_the_whole_line() => Headless.Run(() => WithMainView((view, model) =>
    {
        model.Apply(LiveSnapshot.Empty with
        {
            Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED",
            Constructed = new ConstructedView(5, null, false, "HERO_08", "MAGE", "HERO_01", "WARRIOR", null, [], 30, 4, [], [], 0, 0, 0, null, null),
        });
        Dispatcher.UIThread.RunJobs();
        var line = view.FindControl<TextBlock>("LiveDetailLine")!;
        Assert.EndsWith(Loc.T("Main.Live.DeckUnknown"), model.LiveDetail);
        Assert.Equal(model.LiveDetail, line.Text);
        Assert.Equal(model.LiveDetail, ToolTip.GetTip(line));
    }));

    /// <summary>The main view over a fresh view model with the theme applied, in English.</summary>
    private static void WithMainView(Action<MainView, MainViewModel> body, AppSettings? settings = null, MainViewModel? model = null)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        model ??= new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-ux-polish-{Guid.NewGuid():N}.db")), settings ?? new AppSettings());
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
