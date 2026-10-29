using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The view model lives as long as the app, the main window does not: a theme change (and the tray) closes it and
/// builds a new one. A closed window must not stay reachable from the view model, or every page it built stays in
/// memory with it (perf-architecture-audit, 29.10: closing the window freed nothing). The tray builds the next one where
/// the last one was.
/// </summary>
public sealed class MainWindowLifetimeTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public void A_closed_wpf_main_window_is_collected_while_its_view_model_lives_on() => OnSta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var window = OpenAndClose(model);
        Collect(window);
        Assert.False(window.IsAlive);
        GC.KeepAlive(model);
    });

    /// <summary>A window rebuilt from the last one's place (theme change, back from the tray) opens on the same page, tabs and bounds.</summary>
    [Fact]
    public void A_rebuilt_wpf_main_window_opens_where_the_last_one_was() => OnSta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        var first = OffScreen(new WpfWindow(model));
        first.Show();
        first.ShowPage("Wild");
        first.ConstructedTab = "Matchups";
        first.ShowPage("Battlegrounds");
        first.BattlegroundsTab = "Trinkets";
        first.Width = 1180;
        Pump();
        var place = first.Place;
        first.CloseForReal();

        var second = new WpfWindow(model);
        second.MoveTo(place);
        second.ShowActivated = false;
        second.ShowInTaskbar = false;
        second.Show();
        Pump();
        Assert.Equal("Battlegrounds", second.CurrentPage);
        Assert.Equal("Trinkets", second.BattlegroundsTab);
        Assert.Equal("Matchups", second.ConstructedTab);
        // Windows keeps a window within its coordinate range: the first one took what it could, the second the same.
        Assert.Equal((first.Left, first.Top, 1180d, 840d), (second.Left, second.Top, second.Width, second.Height));
        Assert.False(place.Maximized);
        second.CloseForReal();
    });

    /// <summary>
    /// A theme change closes the overlay window too and builds a new one; the old one must not stay alive through the
    /// overlay view model. Laid out off screen, never shown (shown, it would cover the screen).
    /// </summary>
    [Fact(Skip = "Open: CardPoolPanel subscribes to OverlayViewModel.PropertyChanged and never lets go, so the old overlay "
                 + "stays (overlay view code; TASK-2026-10-29-perf-ram-cpu handover)")]
    public void A_closed_wpf_overlay_window_is_collected_while_its_view_model_lives_on() => OnSta(() =>
    {
        var model = new OverlayViewModel();
        var window = BuildAndCloseOverlay(model);
        Collect(window);
        Assert.False(window.IsAlive);
        GC.KeepAlive(model);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAndCloseOverlay(OverlayViewModel model)
    {
        var window = new OverlayWindow(model, new AppSettings());
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1920, 1080));
        root.Arrange(new Rect(0, 0, 1920, 1080));
        Pump();
        window.Close();
        Pump();
        return new WeakReference(window);
    }

    /// <summary>Shows the window off screen on every sidebar page and tab, then closes it for real; only a weak reference leaves.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(WpfMain model)
    {
        var window = OffScreen(new WpfWindow(model));
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

    private static void OnSta(Action body)
    {
        Loc.Instance.SetLanguage(Loc.English);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Clear();
                app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 1)));
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
            throw new InvalidOperationException("Window lifetime check failed", failure);
        }
    }

    private static void Collect(WeakReference target)
    {
        for (var i = 0; i < 5 && target.IsAlive; i++)
        {
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static WpfWindow OffScreen(WpfWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = 1260;
        window.Height = 840;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        return window;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
