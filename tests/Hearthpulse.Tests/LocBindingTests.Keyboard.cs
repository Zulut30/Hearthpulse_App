using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>Etalon D2: the keyboard in the Avalonia main window (R13).</summary>
public sealed partial class LocBindingTests
{
    /// <summary>
    /// The History list is one Tab stop (Avalonia's ListBox default): Tab leaves it from any game, the arrows move between
    /// games, and the focused game shows the theme's ring rather than Fluent's.
    /// </summary>
    [Fact]
    public void History_list_is_one_tab_stop_with_arrows_inside_and_a_themed_ring() => Headless.Run(() =>
    {
        var theme = Theme();
        var model = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-keyboard-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        var view = new ReplayLibraryView { DataContext = model.Library };
        var cards = Enumerable.Range(1, 3)
            .Select(i => new LibraryCard(ReplayShelf.Recent, $"{i}.hpr", $"key{i}", battlegrounds: false) { Title = "Game " + i, Caption = "Caption", When = "Today" })
            .ToList();
        // The games themselves, without replay files on disk; the commands stay the view model's.
        view.FindControl<ListBox>("List")!.ItemsSource = cards;
        var window = new Window { Width = 1000, Height = 900, Content = view };
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            var rows = view.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is LibraryCard && b.Classes.Contains("libraryCard")).ToList();
            Assert.Equal(3, rows.Count);
            var ring = Assert.IsType<Border>(rows[0].FocusAdorner?.Build());
            Assert.Equal(new CornerRadius(8), ring.CornerRadius);
            Assert.Same(Application.Current!.FindResource("Accent"), ring.BorderBrush);
            Assert.True(rows[0].Focus(NavigationMethod.Tab));
            Assert.Same(rows[0], Focused(window));

            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Assert.Same(cards[1], Focused(window)?.DataContext);

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.IsNotType<LibraryCard>(Focused(window)?.DataContext);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.IsType<LibraryCard>(Focused(window)?.DataContext);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>
    /// Ctrl+1…8 name themselves to screen readers on the sidebar items, and the shortcut moves focus with the page: the
    /// control focus was on may sit on the page just hidden.
    /// </summary>
    [Fact]
    public void Page_shortcuts_are_named_on_the_sidebar_and_take_focus_with_the_page() => Headless.Run(() =>
    {
        // The headless platform has no Inter: the app swaps UiFont at startup the same way (HearthpulseApp).
        var app = Application.Current!;
        app.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        try
        {
            WithMainView(PageShortcuts);
        }
        finally
        {
            app.Resources.Remove("UiFont");
        }
    });

    private static void PageShortcuts(MainView view, MainViewModel model)
    {
        var window = Assert.IsType<Window>(view.Parent);
        window.Show();
        window.Activate();
        Dispatcher.UIThread.RunJobs();
        var items = view.GetLogicalDescendants().OfType<RadioButton>().Where(r => r.Classes.Contains("nav")).ToList();
        Assert.Equal(MainViewModel.NavPages.Count, items.Count);
        Assert.Equal(Enumerable.Range(1, items.Count).Select(i => $"Ctrl+{i}"), items.Select(Avalonia.Automation.AutomationProperties.GetAcceleratorKey));

        window.KeyPressQwerty(PhysicalKey.Digit3, RawInputModifiers.Control);
        Assert.Equal("Standard", view.CurrentPage);
        Assert.Same(view.FindControl<RadioButton>("NavStandard"), Focused(window));
        window.KeyPressQwerty(PhysicalKey.NumPad7, RawInputModifiers.Control);
        Assert.Equal("History", view.CurrentPage);
        Assert.Same(view.FindControl<RadioButton>("NavHistory"), Focused(window));
    }

    private static Control? Focused(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        return window.GetVisualDescendants().OfType<Control>().SingleOrDefault(c => c.IsFocused);
    }
}
