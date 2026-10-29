using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The usability pass (TASK-2026-10-29-ux-polish): what was hidden in tips or easy to hit by mistake, checked in the
/// WPF window laid out off screen with the real theme and fonts, and in both hosts' view models.
/// </summary>
public sealed class UxPolishContractTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void Busy_line_ends_in_an_ellipsis_and_its_tip_carries_the_whole_message() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        // The narrowest window: the line has the least room left of the status chips.
        var (window, root) = Open(model, "Home", width: 1040);
        model.Busy = Loc.T("Host.Busy.GameNotFound");
        Settle(root);

        var line = Named<DockPanel>(window, "BusyLine");
        Assert.Equal(Visibility.Visible, line.Visibility);
        var shown = line.Children.OfType<TextBlock>().Last();
        Assert.Equal(model.Busy, shown.Text);
        Assert.Equal(TextTrimming.CharacterEllipsis, shown.TextTrimming);
        // Trimmed inside its line, not measured at full length and cut off under the chips.
        Assert.True(shown.ActualWidth < MeasureFree(shown), "The long message should be trimmed");
        Assert.True(shown.TranslatePoint(new Point(shown.ActualWidth, 0), line).X <= line.ActualWidth + 0.5);

        var tip = Assert.IsType<StackPanel>(line.ToolTip);
        // A tooltip takes its owner's data context when it opens; off screen it is handed over here.
        tip.DataContext ??= model;
        var texts = tip.Children.OfType<TextBlock>().ToList();
        Assert.Equal(model.Busy, texts[0].Text);
        Assert.Equal(TextWrapping.Wrap, texts[0].TextWrapping);
        Assert.Equal(Loc.T("Main.Busy.Tip"), texts[1].Text);
        window.Close();
    });

    /// <summary>
    /// Every settings card says what it is for, in the same order as the Avalonia view (LocBindingTests.UxPolish); the
    /// leaderboard calibration in the Layout card says it under its own heading (WPF has its Windows wording).
    /// </summary>
    private static readonly string[] CardNotes =
    [
        "Settings.Note.Language", "Settings.Note.Theme", "Settings.Note.App", "Settings.Note.Overlay", "Settings.Note.Layout", "Overlay.Move.Settings",
        "Settings.Leaderboard.Caption", "Settings.Note.Tracker", "Settings.Note.Counters", "Settings.Note.Account", "Settings.Note.Data",
        "Settings.Note.Sources",
    ];

    [Fact]
    public void Settings_cards_say_what_they_are_for_and_the_replay_rule_is_a_visible_line() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Settings", "Data");

        var note = window.FindResource("CardNote");
        var notes = Logical<TextBlock>(Named<ScrollViewer>(window, "PageSettings")).Where(t => ReferenceEquals(t.Style, note)).ToList();
        Assert.Equal(CardNotes.Select(Loc.T), notes.Select(t => t.Text));

        var rule = Named<TextBlock>(window, "ReplayLimitNote");
        // Laid out on the Data tab (the window is never shown, so IsVisible stays false): seen without hovering.
        Assert.True(rule.ActualHeight > 0 && rule.ActualWidth > 0);
        Assert.Equal(Loc.T("Match.Settings.ReplayLimit"), rule.Text);
        Assert.Equal(TextWrapping.Wrap, rule.TextWrapping);
        // The data folder path above it wraps too and no longer hides the rule in its tip.
        var folder = Logical<TextBlock>(window).Single(t => BindingPath(t) == nameof(WpfMain.DataFolder));
        Assert.Equal(TextWrapping.Wrap, folder.TextWrapping);
        Assert.Null(folder.ToolTip);
        window.Close();
    });

    [Fact]
    public void Counter_cross_has_its_own_column_and_leaves_the_art_to_open_the_cards() => Sta(() =>
    {
        var window = OpenOverlay(new WpfOverlay());
        var row = (FrameworkElement)((DataTemplate)window.FindResource("CounterTemplate")).LoadContent();
        var grid = Logical<Grid>(row).First(g => g.ColumnDefinitions.Count > 0);
        var hide = Logical<Button>(grid).Single(b => b.Name == "HideButton");
        Assert.Equal(3, Grid.GetColumn(hide));
        Assert.True(grid.ColumnDefinitions[3].Width.Value >= 20);
        // The art stays alone in the first column: a click there opens the counter's cards.
        Assert.Equal(20, grid.ColumnDefinitions[0].Width.Value);
        Assert.All(grid.Children.OfType<FrameworkElement>().Where(c => Grid.GetColumn(c) == 0), c => Assert.IsNotType<Button>(c));
        window.Close();
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Live_line_says_when_the_deck_panel_waits_for_an_unknown_deck(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        dynamic model = wpf ? new WpfMain(History(), new AppSettings()) : new AvaMain(History(), new AppSettings());
        var unknown = Ranked(new ConstructedView(5, null, false, "HERO_08", "MAGE", "HERO_01", "WARRIOR", null, [], 30, 4, [], [], 0, 0, 0, null, null));
        model.Apply(unknown);
        var detail = (string)model.LiveDetail;
        Assert.EndsWith(" · " + Loc.T("Main.Live.DeckUnknown"), detail);
        // Only about your own deck: the line before it is the one the overlay already showed.
        Assert.StartsWith(Loc.F("Main.Live.CtDetail", WpfOverlay.ClassName("MAGE"), WpfOverlay.ClassName("WARRIOR"), 5), detail);

        model.Apply(Ranked(new ConstructedView(5, "Big Mage", true, "HERO_08", "MAGE", "HERO_01", "WARRIOR", null, [], 30, 4, [], [], 0, 0, 0, null, null)));
        Assert.DoesNotContain(Loc.T("Main.Live.DeckUnknown"), (string)model.LiveDetail);

        // With the deck tracker off the panel is hidden anyway: nothing to explain.
        model.Settings.ShowDeckTracker = false;
        model.Apply(unknown);
        Assert.DoesNotContain(Loc.T("Main.Live.DeckUnknown"), (string)model.LiveDetail);

        static LiveSnapshot Ranked(ConstructedView view) =>
            LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
    }

    /// <summary>
    /// Tab moves through the settings sliders, the deck page's "+N / −N cards" chips and the update button, and the
    /// keyboard focus ring shows where it is (WPF draws a FocusVisualStyle for keyboard focus only). Avalonia's Fluent
    /// controls already draw theirs.
    /// </summary>
    [Fact]
    public void Keyboard_focus_shows_on_sliders_diff_chips_and_the_update_button() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, _) = Open(model, "Settings", "Overlay");
        var sliders = Logical<Slider>(Named<ScrollViewer>(window, "PageSettings")).ToList();
        Assert.NotEmpty(sliders);
        Assert.All(sliders, s => Assert.NotNull(s.FocusVisualStyle));

        var update = Named<Button>(window, "InstallUpdateButton");
        Assert.True(update.Focusable);
        Assert.NotNull(update.FocusVisualStyle);
        window.Close();

        var deck = new Hearthpulse.App.Main.Decks.DeckDetailsView();
        var diff = (Style)deck.Resources["DiffToggle"];
        var ring = diff.Setters.OfType<Setter>().Single(x => x.Property == FrameworkElement.FocusVisualStyleProperty);
        Assert.NotNull(ring.Value);
    });

    [Fact]
    public void Leaderboard_calibration_says_what_it_is_for_and_shows_each_sliders_value() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Settings", "Overlay");
        foreach (var name in new[] { "LeaderboardTopSlider", "LeaderboardSlotSlider", "LeaderboardWidthSlider" })
        {
            var slider = Named<Slider>(window, name);
            slider.Value = 0.08;
            Settle(root);
            var value = ((Grid)slider.Parent).Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 2);
            // WPF formats with the element's language, as the overlay size beside it does.
            Assert.Equal(0.08.ToString("P0", value.Language.GetSpecificCulture()), value.Text);
            Assert.True(value.ActualWidth > 0);
        }

        // The note is a line under the heading, not only the switch's tip.
        var note = Logical<TextBlock>(window).Single(t => t.Text == Loc.T("Settings.Leaderboard.Caption"));
        Assert.Same(window.FindResource("CardNote"), note.Style);
        Assert.True(note.ActualHeight > 0);
        window.Close();
    });

    [Fact]
    public void Overlay_and_tracker_tabs_link_to_each_other_and_an_off_overlay_says_so_at_full_contrast() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Settings", "Overlay");

        Named<Button>(window, "OpenTrackerTab").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Visible, Named<StackPanel>(window, "SettingsTracker").Visibility);
        Assert.Equal(Visibility.Collapsed, Named<StackPanel>(window, "SettingsOverlay").Visibility);
        Assert.Equal("Tracker", model.Settings.SettingsTab);
        Named<Button>(window, "OpenOverlayTab").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Visible, Named<StackPanel>(window, "SettingsOverlay").Visibility);
        Assert.Equal("Overlay", model.Settings.SettingsTab);

        var master = Named<CheckBox>(window, "OverlaySetting");
        var note = Named<TextBlock>(window, "OverlayOffNote");
        var groups = new[] { Named<StackPanel>(window, "OverlayPanelSwitches"), Named<StackPanel>(window, "TrackerPanelSwitches") };
        var switches = groups.SelectMany(Logical<CheckBox>).ToList();
        var headings = Logical<TextBlock>(groups[0]).Where(t => ReferenceEquals(t.Style, window.FindResource("SettingGroupOverlayOff"))).ToList();
        Assert.Equal(3, headings.Count);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.All(headings, h => Assert.Equal(1, h.Opacity));

        master.IsChecked = false;
        Settle(root);
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Equal(Loc.T("Settings.Overlay.OffNote"), note.Text);
        // Only the explanatory section headings dim. The switches keep their theme contrast (Text on the card, AA in
        // ThemeContrastTests): nothing above them is see-through, and they still work for setting panels up ahead.
        Assert.All(headings, h => Assert.True(h.Opacity < 1));
        Assert.All(groups, g => Assert.All(Logical<CheckBox>(g), c => Assert.Equal(1, Opacity(c, card: g.Parent))));
        Assert.All(switches, c => Assert.True(c.IsEnabled));

        master.IsChecked = true;
        Settle(root);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.All(headings, h => Assert.Equal(1, h.Opacity));

        // The live bar's panel buttons stay at full contrast whatever the overlay switch says.
        var quick = Named<ItemsControl>(window, "QuickPanels");
        Named<CheckBox>(window, "OverlaySwitch").IsChecked = false;
        Settle(root);
        Assert.Equal(1, quick.Opacity);
        Assert.All(Descendants<System.Windows.Controls.Primitives.ToggleButton>(quick), b => Assert.True(b.IsEnabled));
        window.Close();

        // The opacity a control is drawn with, up to its card.
        static double Opacity(FrameworkElement element, DependencyObject card)
        {
            var opacity = 1.0;
            for (DependencyObject? e = element; e is not null && !ReferenceEquals(e, card); e = LogicalTreeHelper.GetParent(e))
            {
                opacity *= (e as UIElement)?.Opacity ?? 1;
            }

            return opacity;
        }
    });

    [Fact]
    public void Settings_list_every_keyboard_shortcut_as_the_app_registers_them() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, _) = Open(model, "Settings", "General");
        var card = Named<Border>(window, "ShortcutsCard");
        Assert.True(card.ActualHeight > 0);
        Assert.Same(Named<StackPanel>(window, "SettingsGeneral"), card.Parent);

        // Each row: what it does, and its keys in a pill. App.xaml.cs registers H, L and R with Ctrl+Shift.
        string[] rows = ["Pages", "Back", "Hide", "Layout", "Refresh"];
        var labels = Logical<TextBlock>(card).Where(t => ReferenceEquals(t.Style, window.FindResource("SettingLabel"))).Select(t => t.Text);
        Assert.Equal(rows.Select(r => Loc.T("Settings.Shortcuts." + r)), labels);
        var pill = window.FindResource("Pill");
        var keys = Logical<Border>(card).Where(b => ReferenceEquals(b.Style, pill)).Select(b => ((TextBlock)b.Child).Text);
        Assert.Equal(["Ctrl+1…7", "Esc · Alt+← · mouse Back", "Ctrl+Shift+H", "Ctrl+Shift+L", "Ctrl+Shift+R"], keys);
        // Ctrl+1…7 open the sidebar's pages in order.
        Assert.Equal(7, WpfMain.NavPages.Count);
        window.Close();
    });

    private static GameRecord Game(int number) => new()
    {
        Key = "ux-polish-game-" + number,
        StartedAt = new DateTime(2026, 10, 23, 10, 0, 0).AddMinutes(number),
        EndedAt = new DateTime(2026, 10, 23, 10, 5, 0).AddMinutes(number),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win",
        PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "First deck",
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Getting_started_shows_once_the_history_is_read_empty_and_leaves_with_the_first_game(bool wpf)
    {
        var history = History();
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        // Not before the history is read: a player with games never sees it flash.
        Assert.False((bool)model.ShowGettingStarted);
        await (Task)model.RefreshAsync();
        Assert.True((bool)model.ShowGettingStarted);

        Assert.True(history.Add(Game(0)));
        await (Task)model.RefreshAsync();
        Assert.False((bool)model.ShowGettingStarted);

        // Step 2, the logs: only once Hearthstone is found (it runs) and nothing is missing from its log settings.
        model.Apply(LiveSnapshot.Empty);
        Assert.False((bool)model.LogsReady);
        model.Apply(LiveSnapshot.Empty with { HearthstoneRunning = true, LogConfigMissing = ["Power"] });
        Assert.False((bool)model.LogsReady);
        model.Apply(LiveSnapshot.Empty with { HearthstoneRunning = true });
        Assert.True((bool)model.LogsReady);
    }

    [Fact]
    public async Task Getting_started_steps_follow_the_app_and_its_buttons_run_the_existing_actions()
    {
        var model = new WpfMain(History(), new AppSettings());
        await model.RefreshAsync();
        Sta(() =>
        {
            var (window, root) = Open(model, "Home");
            var card = Named<Border>(window, "GettingStartedCard");
            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.True(card.ActualHeight > 0);
            var steps = ((StackPanel)card.Child).Children.OfType<Grid>().ToList();
            Assert.Equal(3, steps.Count);
            Assert.Equal(new[] { "Step1", "Step2", "Step3" }.Select(k => Loc.T("Main.Home.Start." + k)), steps.Select(g => g.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 1).Text));

            // Hearthstone not running, logs not configured yet: every step waits.
            model.Apply(LiveSnapshot.Empty with { LogConfigMissing = ["Power"] });
            Settle(root);
            Assert.Equal([Waiting, Waiting, Waiting], steps.Select(Glyph));

            // Not found yet: with no install folder nothing reads as missing, yet the logs are not known to be ready.
            model.Apply(LiveSnapshot.Empty);
            Settle(root);
            Assert.Equal([Waiting, Waiting, Waiting], steps.Select(Glyph));
            Assert.Equal(Loc.T("Main.Home.Start.Step2Tip"), steps[1].Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 1).ToolTip);

            // Running with the logs ready: the first two are done; the match is still to be played.
            model.Apply(LiveSnapshot.Empty with { HearthstoneRunning = true });
            Settle(root);
            Assert.Equal([Done, Done, Waiting], steps.Select(Glyph));

            var imports = 0;
            var arranges = 0;
            window.ReimportRequested += () => imports++;
            window.ArrangeOverlayRequested += () => arranges++;
            Named<Button>(window, "GettingStartedImport").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Named<Button>(window, "GettingStartedArrange").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal((1, 1), (imports, arranges));
            window.Close();
        });

        static string Glyph(Grid step) => step.Children.OfType<TextBlock>().Single(t => Grid.GetColumn(t) == 0 && t.Visibility == Visibility.Visible).Text;
    }

    private const string Done = "\U000F05E0";
    private const string Waiting = "\U000F0766";

    /// <summary>
    /// The overlay switched from the live bar or the tray: the Settings page's master switch, and with it the
    /// "overlay is off" note, follows (AppSettings does not notify), as in Avalonia.
    /// </summary>
    [Fact]
    public void Settings_follow_the_overlay_switched_from_the_live_bar_or_the_tray() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Settings", "Overlay");
        var master = Named<CheckBox>(window, "OverlaySetting");
        var note = Named<TextBlock>(window, "OverlayOffNote");
        var saves = 0;
        window.SettingsChanged += () => saves++;

        // The live bar's switch: turned off and clicked.
        var bar = Named<CheckBox>(window, "OverlaySwitch");
        bar.IsChecked = false;
        bar.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Settle(root);
        Assert.False(model.Settings.OverlayEnabled);
        Assert.False(master.IsChecked);
        Assert.Equal(Visibility.Visible, note.Visibility);
        // Synced, not changed again: the click saved once.
        Assert.Equal(1, saves);

        // The tray: the host sets the setting and asks the window to show it.
        model.Settings.OverlayEnabled = true;
        window.SyncOverlaySwitch();
        Settle(root);
        Assert.True(master.IsChecked);
        Assert.True(bar.IsChecked);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.Equal(1, saves);
        window.Close();
    });

    /// <summary>At the default width in Russian the live line is trimmed before its "deck not recognised" tail: the tip has it all.</summary>
    [Fact]
    public void Live_line_tip_carries_the_whole_line_when_the_bar_trims_it() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Home");
        try
        {
            Loc.Instance.SetLanguage(Loc.Russian);
            model.Apply(LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED",
                Constructed = new ConstructedView(5, null, false, "HERO_08", "MAGE", "HERO_01", "WARRIOR", null, [], 30, 4, [], [], 0, 0, 0, null, null),
            });
            Settle(root);
            var line = Named<TextBlock>(window, "LiveDetailLine");
            Assert.EndsWith(Loc.T("Main.Live.DeckUnknown"), model.LiveDetail);
            Assert.Equal(model.LiveDetail, line.Text);
            Assert.Equal(model.LiveDetail, line.ToolTip);
            Assert.True(line.ActualWidth < MeasureFree(line), "The live line should be trimmed at 1260 in Russian");
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
            window.Close();
        }
    });

    // ---- harness ----

    /// <summary>Runs <paramref name="body"/> on its own STA thread (WPF windows need one), in English.</summary>
    private static void Sta(Action body)
    {
        Loc.Instance.SetLanguage(Loc.English);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("WPF check failed", failure);
        }
    }

    /// <summary>Builds the main window with the default theme on a page (and Settings tab) and lays it out, nothing shown.</summary>
    private static (WpfWindow Window, FrameworkElement Root) Open(WpfMain model, string page, string? tab = null, double width = 1260)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
        var window = new WpfWindow(model);
        window.ShowPage(page);
        if (tab is not null)
        {
            window.ShowSettingsTab(tab);
        }

        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, 840));
        root.Arrange(new Rect(0, 0, width, 840));
        Settle(root);
        return (window, root);
    }

    /// <summary>Builds the overlay window with the default theme (nothing shown).</summary>
    private static OverlayWindow OpenOverlay(WpfOverlay model)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
        return new OverlayWindow(model, new AppSettings());
    }

    /// <summary>Lets bindings and deferred layout run, then lays the tree out again.</summary>
    private static void Settle(FrameworkElement root)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();
    }

    private static T Named<T>(FrameworkElement window, string name) where T : class =>
        Assert.IsType<T>(window.FindName(name) ?? throw new InvalidOperationException(name + " is not in the window"));

    /// <summary>The width a text block would take with no limit.</summary>
    private static double MeasureFree(TextBlock block)
    {
        var copy = new TextBlock { Text = block.Text, FontFamily = block.FontFamily, FontSize = block.FontSize, FontWeight = block.FontWeight };
        copy.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return copy.DesiredSize.Width;
    }

    private static string? BindingPath(TextBlock block) => System.Windows.Data.BindingOperations.GetBinding(block, TextBlock.TextProperty)?.Path?.Path;

    /// <summary>Logical descendants: these include the content of collapsed tabs and pages.</summary>
    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Logical<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
