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
        "Settings.Note.Language", "Settings.Note.Theme", "Settings.Note.App", "Settings.Note.Overlay", "Settings.Note.Layout",
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
    public void Overlay_and_tracker_tabs_link_to_each_other_and_an_off_overlay_dims_its_panel_switches() => Sta(() =>
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
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.All(groups, g => Assert.Equal(1, g.Opacity));

        master.IsChecked = false;
        Settle(root);
        Assert.Equal(Visibility.Visible, note.Visibility);
        Assert.Equal(Loc.T("Settings.Overlay.OffNote"), note.Text);
        // Dimmed, not disabled: panels can still be set up before the overlay is turned back on.
        Assert.All(groups, g => Assert.True(g.Opacity < 1));
        Assert.All(groups, g => Assert.True(g.IsEnabled));
        Assert.All(groups.SelectMany(Logical<CheckBox>), c => Assert.True(c.IsEnabled));

        master.IsChecked = true;
        Settle(root);
        Assert.Equal(Visibility.Collapsed, note.Visibility);
        Assert.All(groups, g => Assert.Equal(1, g.Opacity));

        // The live bar's panel buttons follow the live bar's own overlay switch.
        var quick = Named<ItemsControl>(window, "QuickPanels");
        Named<CheckBox>(window, "OverlaySwitch").IsChecked = false;
        Settle(root);
        Assert.True(quick.Opacity < 1);
        Assert.All(Descendants<System.Windows.Controls.Primitives.ToggleButton>(quick), b => Assert.True(b.IsEnabled));
        Named<CheckBox>(window, "OverlaySwitch").IsChecked = true;
        Settle(root);
        Assert.Equal(1, quick.Opacity);
        window.Close();
    });

    /// <summary>
    /// The Battlegrounds panels whose placeholders now say they show only there (LocBindingTests.UxPolish pins the
    /// Avalonia set). Not the Discover row: the note would grow its placeholder onto the mulligan bars below.
    /// </summary>
    private static readonly string[] BgPanels = ["bg-counters", "bg-lobby", "bgbar", "hero-row", "trinket-row", "tavern-row", "odds", "session", "lobby"];

    [Fact]
    public void Layout_mode_names_the_battlegrounds_placeholders_and_its_banner_wraps() => Sta(() =>
    {
        var window = OpenOverlay(new WpfOverlay { LayoutEditing = true });
        var root = (FrameworkElement)window.Content;
        try
        {
            Loc.Instance.SetLanguage(Loc.Russian);
            root.Measure(new Size(1920, 1080));
            root.Arrange(new Rect(0, 0, 1920, 1080));
            Settle(root);

            var panels = Descendants<MovablePanel>(root).ToDictionary(p => p.Id);
            Assert.All(BgPanels, id => Assert.Equal(Loc.T("Overlay.Layout.AppearsInBg"), panels[id].Hint));
            Assert.True(string.IsNullOrEmpty(panels["deck"].Hint));
            Assert.True(string.IsNullOrEmpty(panels["choice-row"].Hint));
            Assert.Equal(Loc.T("Overlay.Layout.AppearsInMenus"), panels["ladder-session"].Hint);
            Assert.Equal(Loc.T("Overlay.Layout.AppearsInArenaDraft"), panels["arena-draft"].Hint);

            // In Russian too (longer notes), no placeholder frame lies on another one, as OverlayXamlLoadTests checks in English.
            var frames = panels.Values
                .Select(p => (p.Id, Part: (FrameworkElement)p.Template.FindName("Placeholder", p)))
                .Select(p => (p.Id, Rect: new Rect(p.Part.TranslatePoint(default, root), p.Part.RenderSize)))
                .ToList();
            var overlaps = frames.SelectMany((f, i) => frames.Skip(i + 1)
                    .Where(g => Rect.Intersect(Rect.Inflate(f.Rect, 3, 3), g.Rect) is { IsEmpty: false, Width: > 3, Height: > 3 })
                    .Select(g => $"{f.Id} / {g.Id}"))
                .ToList();
            Assert.True(overlaps.Count == 0, string.Join(", ", overlaps));

            // Measured with real fonts, in Russian: the hints wrap and the banner fits a 4:3 surface at a 120 % overlay
            // (1440 / 1.2 = 1200 units).
            var banner = Named<Border>(window, "LayoutBanner");
            var hints = Logical<TextBlock>(banner).Where(t => t.Text == Loc.T("Overlay.Layout.Hint") || t.Text == Loc.T("Overlay.Layout.InGameHint")).ToList();
            Assert.Equal(2, hints.Count);
            Assert.All(hints, t => Assert.Equal(TextWrapping.Wrap, t.TextWrapping));
            Assert.True(((FrameworkElement)hints[0].Parent).MaxWidth <= 880);
            Assert.True(banner.ActualWidth is > 0 and <= 1200, $"banner {banner.ActualWidth}");
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
