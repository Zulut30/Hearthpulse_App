using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>Etalon D2: the keyboard in the Avalonia main window (R13, review m3).</summary>
public sealed partial class LocBindingTests
{
    /// <summary>
    /// The History list (R13, review m3): one Tab stop; Up and Down move from game to game, Right goes on to the game's own
    /// buttons (star, Share, delete), Left goes back and Esc returns to the game; the focused game shows the theme's ring
    /// rather than Fluent's; Delete asks before deleting.
    /// </summary>
    [Fact]
    public void History_is_one_tab_stop_with_arrows_between_games_and_into_their_buttons() => Headless.Run(() =>
    {
        var theme = Theme();
        var model = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-keyboard-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        var view = new ReplayLibraryView { DataContext = model.Library };
        var cards = Enumerable.Range(1, 3)
            .Select(i => new LibraryCard(ReplayShelf.Imported, $"{i}.hpr", $"key{i}", battlegrounds: false) { Title = "Game " + i, Caption = "Caption", When = "Today" })
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

            string Stop()
            {
                var focused = Focused(window);
                return focused is Button { DataContext: LibraryCard card } button
                    ? $"{cards.IndexOf(card)}:{(button.Classes.Contains("libraryCard") ? "game" : Avalonia.Automation.AutomationProperties.GetName(button))}"
                    : "out";
            }

            string Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                window.KeyPressQwerty(key, modifiers);
                return Stop();
            }

            Assert.True(rows[0].Focus(NavigationMethod.Tab));
            var star = "0:" + cards[0].StarTip;
            var share = "0:" + Loc.T("Library.Share");
            var delete = "0:" + Loc.T("Library.Delete");
            Assert.Equal([star, share, delete, delete], new[] { Press(PhysicalKey.ArrowRight), Press(PhysicalKey.ArrowRight), Press(PhysicalKey.ArrowRight), Press(PhysicalKey.ArrowRight) });
            Assert.Equal(share, Press(PhysicalKey.ArrowLeft));
            Assert.Equal("0:game", Press(PhysicalKey.Escape));
            Assert.Equal(["1:game", "2:game", "2:game", "1:game"], new[] { Press(PhysicalKey.ArrowDown), Press(PhysicalKey.ArrowDown), Press(PhysicalKey.ArrowDown), Press(PhysicalKey.ArrowUp) });

            // One Tab stop: Tab leaves the list from a game, Shift+Tab comes back to a game.
            Assert.Equal("out", Press(PhysicalKey.Tab));
            Assert.EndsWith(":game", Press(PhysicalKey.Tab, RawInputModifiers.Shift), StringComparison.Ordinal);

            // Delete on a game asks first, in the card, with the focus on "No"; Esc takes the question back.
            Assert.True(rows[1].Focus(NavigationMethod.Tab));
            Press(PhysicalKey.Delete);
            Assert.True(cards[1].Confirming);
            Assert.Equal(Loc.T("Library.ConfirmNo"), (Focused(window) as Button)?.Content);
            Assert.Equal("1:game", Press(PhysicalKey.Escape));
            Assert.False(cards[1].Confirming);
            // Answered with Enter on "No": the question goes, the focus is back on the game.
            Press(PhysicalKey.Delete);
            Assert.Equal("1:game", Press(PhysicalKey.Enter));
            Assert.False(cards[1].Confirming);

            // S, Shift+S and O reach the game through the view's keys (no replay files here: star and share say so).
            var opened = new List<Uri>();
            model.Library.OpenBrowser = url =>
            {
                opened.Add(url);
                return true;
            };
            cards[1].SiteUrl = "https://hearthpulse.net/replay/abc";
            Press(PhysicalKey.O);
            Assert.Equal([new Uri("https://hearthpulse.net/replay/abc")], opened);
            Press(PhysicalKey.S);
            Assert.Equal(Loc.T("Library.Star.Failed"), model.Library.Status);
            Press(PhysicalKey.S, RawInputModifiers.Shift);
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (model.Library.Status != Loc.T("Library.ShareFailed") && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(Loc.T("Library.ShareFailed"), model.Library.Status);
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
