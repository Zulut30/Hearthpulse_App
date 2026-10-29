using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
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

    /// <summary>Every settings card says what it is for, in the same words as the Avalonia view (LocBindingTests.UxPolish).</summary>
    private static readonly string[] CardNotes = ["Language", "Theme", "App", "Overlay", "Layout", "Tracker", "Counters", "Account", "Data", "Sources"];

    [Fact]
    public void Settings_cards_say_what_they_are_for_and_the_replay_rule_is_a_visible_line() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var (window, root) = Open(model, "Settings", "Data");

        var note = window.FindResource("CardNote");
        var notes = Logical<TextBlock>(Named<ScrollViewer>(window, "PageSettings")).Where(t => ReferenceEquals(t.Style, note)).ToList();
        Assert.Equal(CardNotes.Select(n => Loc.T("Settings.Note." + n)), notes.Select(t => t.Text));

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

    /// <summary>Lets bindings and deferred layout run, then lays the tree out again.</summary>
    private static void Settle(FrameworkElement root)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();
    }

    private static T Named<T>(WpfWindow window, string name) where T : class =>
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
