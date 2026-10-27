using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using WpfArchetypeRow = Hearthpulse.App.Main.MetaArchetypeRow;
using WpfCell = Hearthpulse.App.Main.MatchupCellView;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The tier-list views (Main/Meta) resolve their resources at run time: this lays out the WPF main window's Standard
/// tier list and heat map with stats from a fake data API (no network), so every row template is built once, and checks
/// that both builds of the shared view model show the same tiers and cells.
/// </summary>
public sealed class MetaXamlLoadTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    [Fact]
    public async Task Wpf_tier_list_and_heat_map_build_their_rows_and_both_hosts_agree()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var meta = await FakeMeta();
        Assert.NotNull(meta);
        Assert.Equal(3, meta.Matchups.Archetypes.Count);

        var ava = new AvaMain(History(), new AppSettings());
        ava.ShowConstructedFormat("FT_STANDARD");
        ava.SetConstructedMeta(meta);
        var wpf = new WpfMain(History(), new AppSettings());
        wpf.ShowConstructedFormat("FT_STANDARD");
        wpf.SetConstructedMeta(meta);
        Assert.Equal(ava.MetaTierGroups.Select(g => (g.Tier, g.Caption, g.Rows.Count)), wpf.MetaTierGroups.Select(g => (g.Tier, g.Caption, g.Rows.Count)));
        Assert.Equal(ava.MatchupMatrix.SelectMany(r => r.Cells).Select(c => c.Text), wpf.MatchupMatrix.SelectMany(r => r.Cells).Select(c => c.Text));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check(meta);
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
            throw new InvalidOperationException("Meta views failed to load", failure);
        }
    }

    private static void Check(ConstructedMeta meta)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Fel", 0, 0, 1)));
        var model = new WpfMain(History(), new AppSettings());
        var window = new WpfWindow(model);
        window.ShowPage("Standard");
        model.SetConstructedMeta(meta);
        var root = (FrameworkElement)window.Content;

        window.ShowConstructedTab("Tiers");
        Layout(root);
        var rows = Descendants<Button>(root).Where(b => b.DataContext is WpfArchetypeRow && Shown(b)).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.ActualHeight > 0));

        // The table view lists the same rows from the same template.
        model.SetMetaView.Execute("Table");
        Layout(root);
        Assert.Equal(3, Descendants<Button>(root).Count(b => b.DataContext is WpfArchetypeRow && Shown(b)));

        window.ShowConstructedTab("Matchups");
        Layout(root);
        var cells = Descendants<Grid>(root).Where(g => g.DataContext is WpfCell && Shown(g)).ToList();
        Assert.Equal(9, cells.Count);
        window.Close();
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(1260, 840));
        root.Arrange(new Rect(0, 0, 1260, 840));
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();
    }

    /// <summary>Three archetypes, their matrix and nothing else: every other request is a 404, as a slice not collected.</summary>
    private static Task<ConstructedMeta?> FakeMeta()
    {
        var cache = Path.Combine(AppPaths.Root, "meta-views-" + Guid.NewGuid().ToString("N"));
        var data = new HearthpulseData(new HearthpulseApi(new HttpClient(new FakeApi()), "https://api.test/"), cache);
        return ConstructedMeta.LoadAsync(data, CardDb.Empty, "standard", "legend", "past_week", "enUS");
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        private const string Meta = """
            {"data": {"items": [
              {"archetype": "Aggro Warlock", "winrate": 56.4, "popularity": 18.9, "games": 31000},
              {"archetype": "Control Warrior", "winrate": 50.1, "popularity": 6.0, "games": 9000},
              {"archetype": "Quest Priest", "winrate": 46.2, "popularity": 3.1, "games": 4000}]},
             "meta": {"fetched_at": "2026-10-27T12:00:00Z"}}
            """;

        private const string Matchups = """
            {"data": {"total_games": 44000, "archetypes": [
              {"archetype": "Aggro Warlock", "class": "WARLOCK", "games": 31000, "winrate": 56.4, "popularity_pct": 18.9},
              {"archetype": "Control Warrior", "class": "WARRIOR", "games": 9000, "winrate": 50.1, "popularity_pct": 6.0},
              {"archetype": "Quest Priest", "class": "PRIEST", "games": 4000, "winrate": 46.2, "popularity_pct": 3.1}],
             "matchups": [
              {"archetype": "Aggro Warlock", "opponent": "Control Warrior", "winrate": 58.0, "games": 900},
              {"archetype": "Control Warrior", "opponent": "Aggro Warlock", "winrate": 42.0, "games": 900},
              {"archetype": "Quest Priest", "opponent": "Aggro Warlock", "winrate": 39.0, "games": 400}]}}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = path.Contains("v1/hsguru/meta?", StringComparison.Ordinal) ? Meta
                : path.Contains("v1/hsguru/matchups?", StringComparison.Ordinal) ? Matchups
                : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\": \"Not Found\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>
    /// Laid out and not under a collapsed parent (the window is never shown, so IsVisible stays false; PageRowsPanel may
    /// build rows ahead under a collapsed list).
    /// </summary>
    private static bool Shown(FrameworkElement e)
    {
        if (e.ActualHeight <= 0)
        {
            return false;
        }

        for (DependencyObject? p = e; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
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
