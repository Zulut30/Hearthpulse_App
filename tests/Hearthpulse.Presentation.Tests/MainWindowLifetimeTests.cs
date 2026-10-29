using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The view model lives as long as the app, the main window does not: a theme change (and the tray) closes it and
/// builds a new one. A closed window must not stay reachable from the view model, or every page it built stays in
/// memory with it (perf-architecture-audit, 29.10: closing the window freed nothing).
/// </summary>
public sealed class MainWindowLifetimeTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void A_closed_wpf_main_window_is_collected_while_its_view_model_lives_on()
    {
        Loc.Instance.SetLanguage(Loc.English);
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
            throw new InvalidOperationException("The closed main window stayed alive", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
        var model = new WpfMain(History(), new AppSettings());
        var window = OpenAndClose(model);
        for (var i = 0; i < 5 && window.IsAlive; i++)
        {
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(window.IsAlive);
        GC.KeepAlive(model);
    }

    /// <summary>Shows the window off screen on every sidebar page and tab, then closes it for real; only a weak reference leaves.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(WpfMain model)
    {
        var window = new WpfWindow(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1260,
            Height = 840,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        foreach (var page in new[] { "Battlegrounds", "Standard", "Arena", "History", "Settings", "Overview" })
        {
            window.ShowPage(page);
            Pump();
        }

        // The tabs build the stats charts and tier lists; a chart once kept the window through Loc.LanguageChanged.
        window.ShowPage("Battlegrounds");
        foreach (var tab in new[] { "Comps", "Trinkets", "Cards", "Heroes" })
        {
            window.ShowBattlegroundsTab(tab);
            Pump();
        }

        window.ShowPage("Standard");
        foreach (var tab in new[] { "Tiers", "Top", "Matchups", "Decks" })
        {
            window.ShowConstructedTab(tab);
            Pump();
        }

        window.CloseForReal();
        Pump();
        return new WeakReference(window);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
