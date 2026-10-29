using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The dashboard is let go after a while hidden in the tray (Desktop <c>MainWindow.CloseForReal</c>) while its view
    /// model lives on with the app: once the view's data context is cleared, nothing keeps the view and its pages.
    /// </summary>
    [Fact]
    public void A_dashboard_let_go_with_its_data_context_is_collected_while_its_view_model_lives_on() => Headless.Run(DashboardLetGo);

    private static void DashboardLetGo()
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-dashboard-{Guid.NewGuid():N}.db")), new AppSettings());
        try
        {
            var view = BuildAndLetGo(model);
            for (var i = 0; i < 5 && view.IsAlive; i++)
            {
                // The compositor keeps the closed window in a frame it has not sent yet; the app sends it on its next
                // frame, the headless platform only when asked. Without this the window stayed whenever no earlier test
                // on this thread had rendered (1 run of 3 on main).
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.False(view.IsAlive);
            GC.KeepAlive(model);
        }
        finally
        {
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Every page and tab once, then the window closes and the view lets go of the model; only a weak reference leaves.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndLetGo(MainViewModel model)
    {
        // Styles and templates only: the window is not shown, so nothing measures text (the headless run has no fonts).
        var window = new Window { Width = 1260, Height = 840 };
        var view = new MainView { DataContext = model };
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        foreach (var page in new[] { "Battlegrounds", "Standard", "Arena", "History", "Settings", "Overview" })
        {
            view.ShowPage(page);
            Dispatcher.UIThread.RunJobs();
        }

        view.ShowPage("Battlegrounds");
        foreach (var tab in new[] { "Comps", "Trinkets", "Cards", "Mine", "Heroes" })
        {
            view.BattlegroundsTab = tab;
            Assert.Equal(tab, view.BattlegroundsTab);
            Dispatcher.UIThread.RunJobs();
        }

        view.ShowPage("Standard");
        foreach (var tab in new[] { "Tiers", "Top", "Matchups", "Decks" })
        {
            view.ConstructedTab = tab;
            Assert.Equal(tab, view.ConstructedTab);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal("Standard", view.CurrentPage);
        window.Close();
        view.DataContext = null;
        Dispatcher.UIThread.RunJobs();
        return new WeakReference(view);
    }
}
