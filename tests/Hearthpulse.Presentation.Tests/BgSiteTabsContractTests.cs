using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The Battlegrounds tier tabs on the site's lists in both hosts (TASK-2026-11-09-light-ui-site-stats, PC2): the WPF
/// and the Avalonia view model build the same rows, lines and pickers from the same lists; and the WPF window builds
/// a tab's card on the tab's first opening, keeps the containers of rows that stay when a list changes in place, and
/// shows "Solo / Duos" and the MMR slices only on the tabs they belong to. The Avalonia twins of the window checks:
/// <c>Hearthpulse.Tests/BgTabsVirtualizationTests</c>.
/// </summary>
public sealed class BgSiteTabsContractTests
{
    private static readonly string[] Letters = ["S", "A", "B", "C", "D"];
    private static readonly DateTime Fetched = new(2026, 11, 9, 17, 1, 9, DateTimeKind.Utc);

    [Fact]
    public void Both_view_models_build_the_same_tabs_from_the_same_lists()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var wpf = new WpfMain(History(), new AppSettings(), TestApp.Data) { BgClock = () => Fetched.AddHours(6) };
        var ava = new AvaMain(History(), new AppSettings(), TestApp.Data) { BgClock = () => Fetched.AddHours(6) };

        void Same(string what)
        {
            Assert.Equal(Text(wpf.HeroTierRows), Text(ava.HeroTierRows));
            Assert.Equal(Text(wpf.TrinketTierRows), Text(ava.TrinketTierRows));
            Assert.Equal(Text(wpf.CardTierRows), Text(ava.CardTierRows));
            Assert.Equal(Text(wpf.CompTiers), Text(ava.CompTiers));
            Assert.Equal(
                (wpf.HeroTab.ToString(), wpf.TrinketTab.ToString(), wpf.CardTab.ToString(), wpf.CompTab.ToString()),
                (ava.HeroTab.ToString(), ava.TrinketTab.ToString(), ava.CardTab.ToString(), ava.CompTab.ToString()));
            Assert.Equal(
                (wpf.HeroTopLabel, wpf.HeroTopTip, wpf.CardStatsShown, wpf.ShowBgMode, wpf.HasBracketChoice, wpf.CompTabLabel, wpf.CompCaption, wpf.HeroArrows.ToString(), what),
                (ava.HeroTopLabel, ava.HeroTopTip, ava.CardStatsShown, ava.ShowBgMode, ava.HasBracketChoice, ava.CompTabLabel, ava.CompCaption, ava.HeroArrows.ToString(), what));
            Assert.Equal(wpf.BracketOptions.Select(o => (o.Value, o.Label, o.IsSelected)), ava.BracketOptions.Select(o => (o.Value, o.Label, o.IsSelected)));
        }

        // Before a load, and a load that had nothing.
        Same("loading");
        Assert.Equal(Loc.T("Main.Bg.CommunityLoading"), wpf.HeroTab.EmptyText);
        wpf.SetBgSiteStats(null);
        ava.SetBgSiteStats(null);
        Same("unavailable");
        Assert.Equal(Loc.T("Main.Bg.CommunityUnavailable"), wpf.CardTab.EmptyText);

        // The lists: every tab has rows, in the service's tiers.
        var stats = Stats();
        wpf.SetBgSiteStats(stats);
        ava.SetBgSiteStats(stats);
        Same("loaded");
        Assert.Equal(40 + 5, wpf.HeroTierRows.Count);
        Assert.Equal(20 + 5, wpf.CardTierRows.Count);
        Assert.Equal(10 + 5, wpf.TrinketTierRows.Count);
        Assert.Equal(6 + 3, wpf.CompTiers.Count);
        Assert.StartsWith("Hearthpulse data · Solo · top 50", wpf.HeroTab.Source, StringComparison.Ordinal);

        // Every tab, with its picker; a sort; a tavern tier; Duos.
        foreach (var tab in WpfMain.BattlegroundsTabs)
        {
            wpf.BgTabShown(tab);
            ava.BgTabShown(tab);
            Same(tab);
        }

        wpf.BgTabShown("Heroes");
        ava.BgTabShown("Heroes");
        Assert.Equal([50, 20, 5, 1], wpf.BracketOptions.Select(o => o.Value));
        wpf.SetHeroSort.Execute("Placement");
        ava.SetHeroSort.Execute("Placement");
        Same("sorted");
        wpf.SetCardTier.Execute("2");
        ava.SetCardTier.Execute("2");
        Same("tavern tier 2");
        wpf.SetBgMode.Execute("Duos");
        ava.SetBgMode.Execute("Duos");
        Same("duos");
        Assert.Equal(Loc.T("Main.Table.Top2"), wpf.HeroTopLabel);
        Assert.Equal([50, 20], wpf.BracketOptions.Select(o => o.Value));

        // The comp tips are one switch, the same in both UIs, and both follow it: off leaves the rest of the comp.
        Assert.Equal(WpfMain.CompTipsByDefault, AvaMain.CompTipsByDefault);
        Assert.Equal((WpfMain.CompTipsByDefault, AvaMain.CompTipsByDefault), (wpf.ShowCompTips, ava.ShowCompTips));
        wpf.ShowCompTips = true;
        ava.ShowCompTips = true;
        Same("with tips");
        Assert.Contains("Tip 0", Text(wpf.CompTiers), StringComparison.Ordinal);
        wpf.ShowCompTips = false;
        ava.ShowCompTips = false;
        Same("without tips");
        Assert.DoesNotContain("Tip 0", Text(wpf.CompTiers), StringComparison.Ordinal);
        Assert.Contains("Comp 0", Text(wpf.CompTiers), StringComparison.Ordinal);

        static string Text(IEnumerable<object> rows) => string.Join("\n", rows.Select(r => r.ToString()));
    }

    [Fact]
    public void The_WPF_window_builds_a_tier_tabs_card_on_its_first_opening_and_keeps_rows_that_stay() => WpfThread.RunFresh(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data) { BgClock = () => Fetched.AddHours(6) };
        var window = Open(model);
        try
        {
            window.ShowPage("Battlegrounds");
            Pump();
            var tiers = window.PageBattlegrounds.BgTiers;

            // My stats: no card of a tier tab exists, before the lists and after.
            Assert.Equal("Mine", window.BattlegroundsTab);
            Assert.Empty(tiers.BuiltTabs);
            model.SetBgSiteStats(Stats());
            Pump();
            Assert.Empty(tiers.BuiltTabs);
            Assert.Empty(((Grid)tiers.Content).Children);

            // A tab builds its own card and no other; the card has its rows at once.
            window.BattlegroundsTab = "Heroes";
            Pump();
            Assert.Equal(["Heroes"], tiers.BuiltTabs);
            var card = Assert.IsAssignableFrom<FrameworkElement>(Assert.Single(((Grid)tiers.Content).Children));
            Assert.Equal("HeroTierCard", card.Name);
            var list = Assert.IsType<ItemsControl>(card.FindName("HeroTierList"));
            Assert.Same(model.HeroTierRows, list.ItemsSource);
            var panel = Assert.IsType<PageRowsPanel>(VisualChildren(list).OfType<PageRowsPanel>().Single());
            Assert.InRange(panel.Children.Count, 5, 44);
            Assert.True(panel.Children.Count < list.Items.Count, $"{panel.Children.Count} of {list.Items.Count} rows built");

            // Rows of one height, a tier's head as a hero: 46 and the gap of 3 (to the screen's pixels).
            var step = ((FrameworkElement)panel.Children[0]).ActualHeight;
            Assert.InRange(step, 48, 50);
            Assert.All(panel.Children.OfType<FrameworkElement>(), row => Assert.Equal(step, row.ActualHeight));
            Assert.Contains(panel.Children.OfType<ContentPresenter>(), row => row.Content is Hearthpulse.App.Main.TierHeadRow);
            Assert.Contains(panel.Children.OfType<ContentPresenter>(), row => row.Content is Hearthpulse.App.Main.MetaHeroRow);

            // A hero's name is whole in its tooltip and for a screen reader, however much of it the row shows.
            var hero = (Hearthpulse.App.Main.MetaHeroRow)model.HeroTierRows[1];
            var text = VisualChildren(list.ItemContainerGenerator.ContainerFromIndex(1)).OfType<TextBlock>().Single(t => t.Text == hero.Name);
            Assert.Equal((hero.Name, hero.Name), ((string)text.ToolTip, System.Windows.Automation.AutomationProperties.GetName(text)));

            // A sort that moves nothing keeps every container; turned round, the head stays and the heroes change.
            var head = list.ItemContainerGenerator.ContainerFromIndex(0);
            var first = list.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.NotNull(head);
            model.SetHeroSort.Execute("Placement");
            Pump();
            Assert.Same(model.HeroTierRows, list.ItemsSource);
            Assert.Same(head, list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Same(first, list.ItemContainerGenerator.ContainerFromIndex(1));
            model.SetHeroSort.Execute("Placement");
            Pump();
            Assert.Same(head, list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal("H007", ((Hearthpulse.App.Main.MetaHeroRow)((ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(1)).Content).CardId);
            model.SetHeroSort.Execute("Placement");

            // Cards: its own card; another tavern tier keeps the list and the head that stays.
            window.BattlegroundsTab = "Cards";
            Pump();
            Assert.Equal(["Cards", "Heroes"], tiers.BuiltTabs.Order());
            Assert.Equal(Visibility.Collapsed, card.Visibility);
            // The hidden tab's list is collapsed itself: under a collapsed card alone it would still build rows when its
            // list changes (a refresh, a language change).
            Assert.Equal(Visibility.Collapsed, list.Visibility);
            var cards = Assert.IsType<ItemsControl>(((FrameworkElement)((Grid)tiers.Content).Children[1]).FindName("CardTierList"));
            var cardHead = cards.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.NotNull(cardHead);
            model.SetCardTier.Execute("2");
            Pump();
            Assert.Same(model.CardTierRows, cards.ItemsSource);
            Assert.Same(cardHead, cards.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal("M2_000", ((Hearthpulse.App.Main.MetaCardRow)((ContentPresenter)cards.ItemContainerGenerator.ContainerFromIndex(1)).Content).CardId);

            // Back to Heroes: the same card again, with its list.
            window.BattlegroundsTab = "Heroes";
            Pump();
            Assert.Same(card, ((Grid)tiers.Content).Children[0]);
            Assert.Equal((Visibility.Visible, Visibility.Visible), (card.Visibility, list.Visibility));

            // My stats again: every card and list is hidden, none is built.
            window.BattlegroundsTab = "Mine";
            Pump();
            Assert.Equal(2, tiers.BuiltTabs.Count);
            Assert.All(((Grid)tiers.Content).Children.OfType<FrameworkElement>(), c => Assert.Equal(Visibility.Collapsed, c.Visibility));
            Assert.Equal(Visibility.Collapsed, list.Visibility);
        }
        finally
        {
            window.CloseForReal();
            Pump();
        }
    });

    [Fact]
    public void The_WPF_page_shows_Solo_Duos_and_the_slices_only_on_their_tabs() => WpfThread.RunFresh(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data) { BgClock = () => Fetched.AddHours(6) };
        var window = Open(model);
        try
        {
            window.ShowPage("Battlegrounds");
            model.SetBgSiteStats(Stats());
            Pump();
            var page = window.PageBattlegrounds;
            var mode = Parent<Border>(Logical<RadioButton>(page).First(r => r.GroupName == "BgMode"));
            var picker = page.BracketPicker;

            foreach (var (tab, there, slices) in new[] { ("Mine", true, 0), ("Heroes", true, 4), ("Comps", false, 0), ("Trinkets", false, 4), ("Cards", false, 0) })
            {
                window.BattlegroundsTab = tab;
                Pump();
                Assert.Equal(there, mode.IsVisible);
                Assert.Equal(slices > 0, picker.IsVisible);
                Assert.Equal(slices, picker.IsVisible ? VisualChildren(picker).OfType<RadioButton>().Count() : 0);
            }

            // Duos on Heroes: two slices and "Top 2".
            window.BattlegroundsTab = "Heroes";
            model.SetBgMode.Execute("Duos");
            Pump();
            Assert.Equal(2, VisualChildren(picker).OfType<RadioButton>().Count());
            Assert.Contains(VisualChildren(page).OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top2") && t.IsVisible);
            model.SetBgMode.Execute("Battlegrounds");

            // Without a Duos list the switch is for My stats only, and Heroes shows solo under "Top 4".
            model.SetBgSiteStats(Stats() with { HeroesDuos = null });
            Pump();
            Assert.False(mode.IsVisible);
            Assert.Contains(VisualChildren(page).OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top4") && t.IsVisible);
            window.BattlegroundsTab = "Mine";
            Pump();
            Assert.True(mode.IsVisible);

            // Nothing could be loaded: the tab says so in place of the list, and has no picker.
            window.BattlegroundsTab = "Heroes";
            model.SetBgSiteStats(null);
            Pump();
            Assert.False(picker.IsVisible);
            Assert.Contains(VisualChildren(page).OfType<ContentControl>(), c => Equals(c.Content, Loc.T("Main.Bg.CommunityUnavailable")) && c.IsVisible);
            Assert.DoesNotContain(VisualChildren(page).OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top4") && t.IsVisible);
        }
        finally
        {
            window.CloseForReal();
            Pump();
        }
    });

    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>
    /// 40 solo heroes and 20 Duos ones, 20 minions on each of two tavern tiers with numbers, 10 trinkets and 6 comps in
    /// three tiers; the average places rise through each list.
    /// </summary>
    private static BgSiteStats Stats()
    {
        BgSiteTierEntry[] Heroes(string prefix, int count) => Enumerable.Range(0, count)
            .Select(i => new BgSiteTierEntry($"{prefix}{i:000}", i, Letters[i * 5 / count], $"Герой {i}", $"Hero {i}", 0, 3.2 + (i * 0.05), 0.9 - (i * 0.01), 0.7 - (i * 0.005), 0.3 - (i * 0.004), null))
            .ToArray();
        var minions = Enumerable.Range(0, 40)
            .Select(i => new BgSiteTierEntry($"M{(i % 2) + 1}_{i / 2:000}", i, Letters[i / 2 / 4], $"Существо {i}", $"Minion {i}", (i % 2) + 1, null, 0.2 - (i * 0.002), null, null, 10_000))
            .ToArray();
        var trinkets = Enumerable.Range(0, 10)
            .Select(i => new BgSiteTierEntry($"T{i:000}", i, Letters[i / 2], $"Безделушка {i}", $"Trinket {i}", 0, 2.0 + (i * 0.1), 0.2 - (i * 0.01), null, null, i, i % 2 == 0 ? "SMALL" : "LARGE", i % 4))
            .ToArray();
        var comps = Enumerable.Range(0, 6)
            .Select(i => new BgSiteComp($"c{i}", $"Comp {i}", string.Empty, Letters[i / 2], [], [], null, null, "medium", "Tip " + i.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        BgSiteTierList List(BgSiteList kind, BgSiteTierEntry[] entries, string? mmr = null, string? range = null) =>
            new(kind, entries, Letters, Fetched, "hsreplay", mmr, range, null);
        return new BgSiteStats(
            List(BgSiteList.Heroes, Heroes("H", 40), "TOP_50_PERCENT", "CURRENT_BATTLEGROUNDS_PATCH"),
            List(BgSiteList.Heroes, Heroes("D", 20), "TOP_50_PERCENT", "CURRENT_BATTLEGROUNDS_PATCH"),
            List(BgSiteList.Minions, minions),
            null,
            List(BgSiteList.Trinkets, trinkets, "TOP_1_PERCENT", "LAST_7_DAYS"),
            minions.ToDictionary(m => m.CardId, m => new BgSiteCardStat(m.CardId, m.DbfId, m.TavernTier, false, 0.4 - (m.DbfId * 0.02), 0.55, 0.08, 5000, 90_000, 3.9, 4.3)),
            new Dictionary<string, BgSiteCardStat>(),
            comps,
            new BgSiteRun("TOP_50_PERCENT", "CURRENT_BATTLEGROUNDS_PATCH", Fetched),
            Fetched,
            Fetched);
    }

    private static WpfWindow Open(WpfMain model)
    {
        var window = new WpfWindow(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1260, Height = 840,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T Parent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var at = LogicalTreeHelper.GetParent(child); at is not null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} above the element.");
    }

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

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in VisualChildren(child))
            {
                yield return nested;
            }
        }
    }
}
