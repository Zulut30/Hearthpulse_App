using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main;
using Hearthpulse.App.Main.Decks;
using Hearthpulse.App.Main.Stats;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF deck page, My Stats views and setup wizard resolve their resources only at run time: a missing key in a template builds
/// fine and fails when the page opens. This loads each view with the theme and Main/MainLook.xaml, with rows in its
/// lists and empty lists, and lays it out off screen so every template is built once.
/// </summary>
public sealed class StatsXamlLoadTests
{
    [Fact]
    public void Wpf_deck_page_and_my_stats_views_load_with_the_theme_and_lay_out()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
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
            throw new InvalidOperationException("Deck/stats XAML failed to load", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Hearthpulse;component/Main/MainLook.xaml") });

        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        for (var i = 0; i < 6; i++)
        {
            history.Add(new GameRecord
            {
                Key = "stats-xaml-" + i,
                StartedAt = DateTime.Now.AddMinutes(-100 + (i * 10)),
                EndedAt = DateTime.Now.AddMinutes(-92 + (i * 10)),
                Mode = i < 3 ? "Ranked" : "Battlegrounds",
                GameType = i < 3 ? "GT_RANKED" : "GT_BATTLEGROUNDS",
                Format = i < 3 ? "FT_STANDARD" : string.Empty,
                Result = i % 2 == 0 ? "Win" : "Loss",
                PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "Load deck",
                DeckCode = i < 3 ? DeckLookContractTests.Code((1001, 2), (1002, 2), (1003, 1), (1004, 1)) : null,
                Placement = i < 3 ? 0 : i, RatingAfter = 6000 + i,
            });
        }

        var main = new MainViewModel(history, new AppSettings(), TestApp.Data);
        var refresh = main.RefreshAsync();
        PumpUntil(() => refresh.IsCompleted);
        var deck = new DeckDetailsViewModel(main);
        var families = DeckFamilies.Build(DeckFamilies.Versions(history.Recent(100).Where(g => g.Mode == "Ranked").ToList()), []);
        deck.Show(families.Single(), families, [], null, DeckLookContractTests.Cards);
        PumpUntil(() => deck.DeckList.Count > 0 && deck.MulliganRows.Count > 0);
        Assert.NotEmpty(deck.DeckList);

        // With rows, then with an empty main view model (every empty state shown).
        foreach (var model in new object[] { main, new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db")), new AppSettings(), TestApp.Data) })
        {
            foreach (var view in new FrameworkElement[] { new ConstructedStatsView(), new BattlegroundsStatsView(), new ArenaStatsView() })
            {
                Lay(view, model);
            }
        }

        // The setup wizard on each step: built before the window takes it in, it must bring the dictionaries it uses (the
        // logo is not in the app's resources; a missing one stopped the app at start behind an error window).
        for (var step = 0; step < MainViewModel.SetupStepKeys.Count; step++)
        {
            main.ShowSetup(step);
            Lay(new Hearthpulse.App.Main.Setup.SetupWizard(), main);
        }

        var page = new DeckDetailsView();
        Lay(page, deck);

        // The mulligan and card tables take their rows' height, not the room left on the page (a few rows, no gap). They
        // share one place behind two tabs: each is checked on its own tab.
        Assert.NotEmpty(deck.MulliganRows);
        CheckRows(page, deck.MulliganRows);
        page.TabDeckCards.IsChecked = true;
        page.UpdateLayout();
        PumpUntil(() => true);
        page.UpdateLayout();
        CheckRows(page, deck.PerformanceRows);
    }

    private static void CheckRows(DeckDetailsView page, object rowsSource)
    {
        var list = Descendants<ItemsControl>(page).Single(l => l.ItemsSource == rowsSource);
        var rows = Descendants<Border>(list).Where(b => b.DataContext is DeckCardStatRow && b.TemplatedParent is ContentPresenter).ToList();
        Assert.NotEmpty(rows);
        Assert.True(list.ActualHeight <= rows.Sum(r => r.ActualHeight + r.Margin.Bottom) + 1,
            $"{list.ActualHeight} for {rows.Count} rows of {rows[0].ActualHeight}");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static void Lay(FrameworkElement view, object model)
    {
        view.DataContext = model;
        var host = new Border { Child = view };
        host.Measure(new Size(1180, 4000));
        host.Arrange(new Rect(0, 0, 1180, 4000));
        PumpUntil(() => true);
        host.UpdateLayout();
        Assert.True(view.ActualHeight > 0, view.GetType().Name);
    }

    /// <summary>
    /// Runs the dispatcher until <paramref name="done"/> holds (the deck page rebuilds on a worker and lands back here),
    /// at most 10 s, then once more at idle so bindings and layout have applied.
    /// </summary>
    private static void PumpUntil(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Idle();
            Thread.Yield();
        }

        Assert.True(done(), "the view model did not finish its rebuild in 10 s");
        Idle();
    }

    private static void Idle()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
