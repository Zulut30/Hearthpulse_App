using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Battlegrounds tier tabs in the Avalonia main view (TASK-2026-11-09-light-ui-site-stats, PC2): a tab's card is
/// built when the tab is first opened, its list builds the rows near what the page shows, a list that changes keeps
/// the rows that stay, and "Solo / Duos" and the MMR slices show only on the tabs they belong to.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class BgTabsVirtualizationTests
{
    public BgTabsVirtualizationTests() => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.English));

    [Fact]
    public void Heroes_and_cards_build_the_rows_near_the_viewport_and_the_last_row_after_a_scroll_to_the_end() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(1280);
        try
        {
            model.SetBgSiteStats(Long());
            foreach (var (tab, name, count) in new[] { ("Heroes", "HeroTierList", 120 + 5), ("Cards", "CardTierList", 60 + 5) })
            {
                view.ShowBattlegroundsTab(tab);
                window.UpdateLayout();
                var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == name);
                var panel = Assert.IsType<PageRowsPanel>(list.ItemsPanelRoot);
                var page = Assert.IsType<ScrollViewer>(list.FindAncestorOfType<ScrollViewer>());
                page.Offset = default;
                PageRowsPanelAvaloniaTests.Settle(window);
                Assert.Equal(count, list.ItemCount);

                // Every row has one height, a tier's head as a data row: 46 and the gap of 2.
                var step = panel.Children[0].Bounds.Height;
                Assert.Equal(48, step);
                Assert.All(panel.Children, row => Assert.Equal(step, row.Bounds.Height));

                // A row's plate stands 8 px out of the list on each side, so its columns stay under the heads, and the
                // list does not cut it (the tier colour down a head's left edge is in that overhang).
                Assert.False(list.ClipToBounds);
                var plate = panel.Children[1].GetVisualDescendants().OfType<Border>().First();
                Assert.Equal((-8, list.Bounds.Width + 16, 46), (plate.Bounds.X, plate.Bounds.Width, plate.Bounds.Height));

                // Built: no more than the rows the page shows and two screens of them, and the end of the list is not.
                var shown = panel.Children.Count(row => row.TranslatePoint(default, page) is { } at && at.Y + step > 0 && at.Y < page.Viewport.Height);
                var screen = (int)Math.Ceiling(page.Viewport.Height / step);
                Assert.InRange(shown, 3, screen);
                Assert.True(panel.Children.Count <= shown + (2 * screen), $"{tab}: {panel.Children.Count} rows built, {shown} shown, {screen} to a screen");
                Assert.True(panel.Children.Count < list.ItemCount, $"{tab}: {panel.Children.Count} of {list.ItemCount} rows built");
                Assert.Null(list.ContainerFromIndex(count - 1));

                // At the end of the page the last row is built and in sight.
                page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
                PageRowsPanelAvaloniaTests.Settle(window);
                var last = Assert.IsType<ContentPresenter>(list.ContainerFromIndex(count - 1));
                var top = last.TranslatePoint(default, page)!.Value.Y;
                Assert.InRange(top, 0, page.Viewport.Height - 1);
                Assert.True(last.IsEffectivelyVisible);
                page.Offset = default;
            }
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void A_tier_tabs_card_is_built_when_the_tab_is_first_opened_and_kept_after() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(1280);
        try
        {
            var tiers = view.PageBattlegrounds.BgTiers;

            // The page opens on My stats: no card of a tier tab exists, with or without data.
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Empty(tiers.BuiltTabs);
            model.SetBgSiteStats(Long());
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Empty(tiers.BuiltTabs);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name is "HeroTierCard" or "CompTierCard" or "TrinketTierCard" or "CardStatsCard");

            // Each tab builds its own card, with its rows at once (the default slice is in memory), and no other.
            string[] opened = [];
            foreach (var (tab, card) in new[] { ("Cards", "CardStatsCard"), ("Heroes", "HeroTierCard"), ("Trinkets", "TrinketTierCard"), ("Comps", "CompTierCard") })
            {
                view.ShowBattlegroundsTab(tab);
                PageRowsPanelAvaloniaTests.Settle(window);
                opened = [.. opened, tab];
                Assert.Equal(opened.Order(), tiers.BuiltTabs.Order());
                var cards = tiers.GetVisualDescendants().OfType<Border>().Where(b => b.Name is { Length: > 0 } n && n.EndsWith("Card", StringComparison.Ordinal)).ToList();
                Assert.Equal([card], cards.Where(b => b.IsEffectivelyVisible).Select(b => b.Name));
                Assert.NotEmpty(cards.Single(b => b.Name == card).GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name is { Length: > 0 } n && n.EndsWith("List", StringComparison.Ordinal)).GetRealizedContainers());
            }

            // Back to My stats and to a tab again: the same card, not a new one.
            var heroes = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeroTierCard");
            view.BattlegroundsTab = "Mine";
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.False(heroes.IsEffectivelyVisible);
            view.ShowBattlegroundsTab("Heroes");
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Same(heroes, view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeroTierCard"));
            Assert.True(heroes.IsEffectivelyVisible);
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void A_sort_or_another_tavern_tier_keeps_the_rows_that_stay_where_they_stood() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(1280);
        try
        {
            model.SetBgSiteStats(Long());
            view.ShowBattlegroundsTab("Heroes");
            PageRowsPanelAvaloniaTests.Settle(window);
            var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HeroTierList");
            var source = list.ItemsSource;
            var head = list.ContainerFromIndex(0);
            var first = list.ContainerFromIndex(1);
            Assert.NotNull(head);

            // The pick rates fall through the list, so sorting by them high to low is the service's order: nothing moves
            // and every row keeps its container. Turned round, the head stays and the heroes under it change.
            model.SetHeroSort.Execute("Pick");
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Same(source, list.ItemsSource);
            Assert.Same(head, list.ContainerFromIndex(0));
            Assert.Same(first, list.ContainerFromIndex(1));
            model.SetHeroSort.Execute("Pick");
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Same(source, list.ItemsSource);
            Assert.Same(head, list.ContainerFromIndex(0));
            Assert.Equal("H023", ((MetaHeroRow)((ContentPresenter)list.ContainerFromIndex(1)!).Content!).CardId);

            view.ShowBattlegroundsTab("Cards");
            PageRowsPanelAvaloniaTests.Settle(window);
            var cards = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "CardTierList");
            var cardSource = cards.ItemsSource;
            var cardHead = cards.ContainerFromIndex(0);
            model.SetCardTier.Execute("2");
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Same(cardSource, cards.ItemsSource);
            // Tier 2 has the same first head ("Tier S", twelve minions): the row stays, the minions under it are others.
            Assert.Same(cardHead, cards.ContainerFromIndex(0));
            Assert.Equal("M2_000", ((MetaCardRow)((ContentPresenter)cards.ContainerFromIndex(1)!).Content!).CardId);
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void Solo_Duos_and_the_slices_show_only_on_the_tabs_they_belong_to() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(1280);
        try
        {
            var page = view.PageBattlegrounds;
            var mode = page.GetVisualDescendants().OfType<RadioButton>().First(r => r.GroupName == "BgMode").FindAncestorOfType<Border>()!;
            var picker = page.BracketPicker;
            var place = mode.Bounds;

            // "Solo / Duos" is there when it is in sight and can be reached; "away" it keeps its room (in the compact
            // layout it is a row of its own above the tabs, which must not move under the pointer).
            void Assert_mode(bool there)
            {
                PageRowsPanelAvaloniaTests.Settle(window);
                Assert.Equal(there, model.ShowBgMode);
                Assert.Equal(there, mode.Opacity > 0 && mode.IsHitTestVisible && mode.IsEffectivelyEnabled);
                Assert.True(mode.IsVisible);
                Assert.Equal(place, mode.Bounds);
            }

            // With a Duos list: on My stats and on Heroes.
            model.SetBgSiteStats(Long());
            foreach (var (tab, there, slices) in new[] { ("Mine", true, 0), ("Heroes", true, 4), ("Comps", false, 0), ("Trinkets", false, 4), ("Cards", false, 0) })
            {
                view.BattlegroundsTab = tab;
                Assert_mode(there);
                Assert.Equal(slices > 0, picker.IsEffectivelyVisible);
                Assert.Equal(slices, picker.IsEffectivelyVisible ? picker.GetVisualDescendants().OfType<RadioButton>().Count() : 0);
            }

            // Duos on Heroes: two slices, and the head of the top column says "Top 2".
            view.BattlegroundsTab = "Heroes";
            model.SetBgMode.Execute("Duos");
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.Equal(2, picker.GetVisualDescendants().OfType<RadioButton>().Count());
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top2") && t.IsEffectivelyVisible);
            model.SetBgMode.Execute("Battlegrounds");

            // Without a Duos list: on My stats only; Heroes shows solo under "Top 4".
            model.SetBgSiteStats(Long() with { HeroesDuos = null });
            view.BattlegroundsTab = "Mine";
            Assert_mode(true);
            view.BattlegroundsTab = "Heroes";
            Assert_mode(false);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top4") && t.IsEffectivelyVisible);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.Top2") && t.IsEffectivelyVisible);

            // Nothing loaded: no picker anywhere, and the tab says so instead of a list.
            model.SetBgSiteStats(null);
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.False(picker.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<ContentControl>(), c => Equals(c.Content, Loc.T("Main.Bg.CommunityUnavailable")) && c.IsEffectivelyVisible);
            Assert.False(view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HeroTierList").IsEffectivelyVisible);
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void The_compact_layout_drops_the_widest_columns_of_heroes_and_cards() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(860);
        try
        {
            model.SetBgSiteStats(Long());
            Assert.Contains("compact", view.Classes);

            view.ShowBattlegroundsTab("Heroes");
            PageRowsPanelAvaloniaTests.Settle(window);
            var hero = (MetaHeroRow)model.HeroTierRows[1];
            var heroRow = Row(view, "HeroTierList", 1);
            // First places and pick rate go, with their heads; rank, name, place, top share and "You" stay.
            Assert.DoesNotContain(heroRow, t => t.Text == hero.FirstPlace || t.Text == hero.PickRate);
            Assert.Contains(heroRow, t => t.Text == hero.Top);
            Assert.Contains(heroRow, t => t.Text == hero.AveragePlace);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Table.First") && t.IsEffectivelyVisible);

            view.ShowBattlegroundsTab("Cards");
            PageRowsPanelAvaloniaTests.Settle(window);
            var card = (MetaCardRow)model.CardTierRows[1];
            var cardRow = Row(view, "CardTierList", 1);
            // "With · without" and popularity go; impact and combat win rate stay.
            Assert.DoesNotContain(cardRow, t => t.Text == card.PlacementSplit || t.Text == card.Popularity);
            Assert.Contains(cardRow, t => t.Text == card.Impact);
            Assert.Contains(cardRow, t => t.Text == card.CombatWinRate);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Bg.PlacementSplit") && t.IsEffectivelyVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Main.Bg.Impact") && t.IsEffectivelyVisible);

            static List<TextBlock> Row(MainView view, string list, int index) =>
                view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == list).ContainerFromIndex(index)!
                    .GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void At_the_narrowest_window_the_names_of_heroes_and_trinkets_keep_their_room() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(420);
        try
        {
            model.SetBgSiteStats(Long());
            Assert.Contains("compact", view.Classes);

            foreach (var (tab, list) in new[] { ("Heroes", "HeroTierList"), ("Trinkets", "TrinketTierList") })
            {
                view.ShowBattlegroundsTab(tab);
                PageRowsPanelAvaloniaTests.Settle(window);
                var items = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == list);
                var row = items.ContainerFromIndex(1)!;
                var name = row.DataContext is MetaHeroRow hero ? hero.Name : ((MetaTrinketRow)row.DataContext!).Name;
                var text = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == name);

                // The name has room for a word or two and stands inside the row; the rank and the bar are gone, the
                // art and the number stay.
                Assert.Contains("narrow", view.PageBattlegrounds.BgTiers.Classes);
                Assert.True(text.IsEffectivelyVisible);
                Assert.True(text.Bounds.Width >= 100, $"{tab}: the name is {text.Bounds.Width:0} wide in a list of {items.Bounds.Width:0}");
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<Control>(), c => c.Classes.Contains("bgNarrowOff") && c.IsEffectivelyVisible);
                Assert.Contains(row.GetVisualDescendants().OfType<Border>(), b => b.Width is 36 or 54 && b.IsEffectivelyVisible);
                // A name cut with an ellipsis is whole in its tooltip and for a screen reader.
                Assert.Equal(name, ToolTip.GetTip(text));
                Assert.Equal(name, Avalonia.Automation.AutomationProperties.GetName(text));
                // Nothing of the row stands outside it.
                Assert.All(row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                    t => Assert.True(t.TranslatePoint(new Point(t.Bounds.Width, 0), row)!.Value.X <= row.Bounds.Width + 0.5, $"{tab}: \"{t.Text}\" ends past the row"));
                var place = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("bgPlace"));
                Assert.True(place.IsEffectivelyVisible);
                Assert.Equal(72, place.Bounds.Width);
                // The row and its head agree on the columns: the number stands under "Avg placement".
                var head = view.GetVisualDescendants().OfType<Control>().Single(c => c.Classes.Contains("bgPlace") && c.IsEffectivelyVisible && c.FindAncestorOfType<ItemsControl>() is null);
                Assert.Equal(head.TranslatePoint(default, view)!.Value.X, place.TranslatePoint(default, view)!.Value.X, 1.0);
            }

            // Cards: the art, the name and the impact stay; the rank and the combat win rate go with the two columns
            // compact already drops.
            view.ShowBattlegroundsTab("Cards");
            PageRowsPanelAvaloniaTests.Settle(window);
            var cardRow = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "CardTierList").ContainerFromIndex(1)!;
            var card = (MetaCardRow)cardRow.DataContext!;
            var shown = cardRow.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            Assert.Equal((card.Name, card.Name), ((string?)ToolTip.GetTip(shown[0]), Avalonia.Automation.AutomationProperties.GetName(shown[0])));
            Assert.Equal([card.Name, card.Detail, card.Impact], shown.Select(t => t.Text));
            Assert.True(shown[0].Bounds.Width >= 100, $"Cards: the name is {shown[0].Bounds.Width:0} wide");

            // Wide again: everything is back and the cell has its full width.
            window.Width = 1280;
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.DoesNotContain("compact", view.Classes);
            Assert.DoesNotContain("narrow", view.PageBattlegrounds.BgTiers.Classes);
            view.ShowBattlegroundsTab("Trinkets");
            PageRowsPanelAvaloniaTests.Settle(window);
            var wide = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "TrinketTierList").ContainerFromIndex(1)!;
            Assert.Equal(192, wide.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("bgPlace")).Bounds.Width);
            Assert.All(wide.GetVisualDescendants().OfType<Control>().Where(c => c.Classes.Contains("bgNarrowOff")), c => Assert.True(c.IsEffectivelyVisible));
        }
        finally
        {
            Close(window, theme);
        }
    });

    [Fact]
    public void A_comp_without_its_tip_leaves_no_room_for_it() => LocBindingTests.Headless.Run(() =>
    {
        var theme = PageRowsPanelAvaloniaTests.Theme();
        var (window, view, model) = Open(1280);
        try
        {
            model.SetBgSiteStats(Long());
            view.ShowBattlegroundsTab("Comps");
            PageRowsPanelAvaloniaTests.Settle(window);
            var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "CompTierList");
            Control Row() => list.GetRealizedContainers().First(c => c.DataContext is MetaCompRow);

            // Tips are off: the row is its name and difficulty, as high as a data row, and the tip's text takes no room.
            var comp = (MetaCompRow)Row().DataContext!;
            Assert.Equal(string.Empty, comp.Tip);
            Assert.Equal(48, Row().Bounds.Height);
            Assert.DoesNotContain(Row().GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && string.IsNullOrEmpty(t.Text));
            Assert.Equal(2, Row().GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible));

            // With tips on the same row grows by the tip's line.
            model.ShowCompTips = true;
            PageRowsPanelAvaloniaTests.Settle(window);
            Assert.True(Row().Bounds.Height > 48, $"the row is {Row().Bounds.Height} high with its tip");
            Assert.Contains(Row().GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == ((MetaCompRow)Row().DataContext!).Tip);
        }
        finally
        {
            Close(window, theme);
        }
    });

    /// <summary>The main view on the Battlegrounds page (My stats), in a window of the given width.</summary>
    private static (Window Window, MainView View, MainViewModel Model) Open(double width)
    {
        var model = new MainViewModel(new HistoryStore(TestDataFolder.Scratch("hp-bg-tabs") + ".db"), new AppSettings(), TestDataFolder.App);
        var view = new MainView { DataContext = model };
        var window = new Window { Width = width, Height = 800 };
        // The headless platform has no Inter: the app swaps UiFont at startup the same way (HearthpulseApp).
        window.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        window.Content = view;
        window.Show();
        view.ShowPage("Battlegrounds");
        PageRowsPanelAvaloniaTests.Settle(window);
        return (window, view, model);
    }

    private static void Close(Window window, Avalonia.Styling.Styles theme)
    {
        window.Close();
        Application.Current!.Styles.Remove(theme);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Lists as long as the service's: 120 solo heroes and 40 Duos ones in five tiers, 60 minions on each of two
    /// tavern tiers with numbers, 30 trinkets and 9 comps. The pick rates fall through each list.
    /// </summary>
    internal static BgSiteStats Long()
    {
        string[] letters = ["S", "A", "B", "C", "D"];
        BgSiteTierEntry[] Heroes(string prefix, int count) => Enumerable.Range(0, count)
            .Select(i => BgSiteTabsTests.Hero($"{prefix}{i:000}", letters[i * 5 / count], 3.2 + (i * 0.02), top4: 0.7 - (i * 0.003), first: 0.3 - (i * 0.002), pick: 0.9 - (i * 0.005)))
            .ToArray();
        var minions = Enumerable.Range(0, 120)
            .Select(i => BgSiteTabsTests.Entry($"M{(i % 2) + 1}_{i / 2:000}", letters[i / 2 * 5 / 60], tavern: (i % 2) + 1, pick: 0.2 - (i * 0.001), games: 10_000))
            .ToArray();
        var trinkets = Enumerable.Range(0, 30)
            .Select(i => new BgSiteTierEntry($"T{i:000}", i, letters[i / 6], $"Безделушка {i}", $"Trinket {i}", 0, 2.0 + (i * 0.1), 0.2 - (i * 0.005), null, null, i, i % 2 == 0 ? "SMALL" : "LARGE", i % 4))
            .ToArray();
        var comps = Enumerable.Range(0, 9)
            .Select(i => new BgSiteComp($"c{i}", $"Сборка {i}", string.Empty, letters[i / 3], [], [], null, null, "medium", "Совет " + i.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var stats = minions.Select((m, i) => new BgSiteCardStat(m.CardId, i, m.TavernTier, false, 0.6 - (i * 0.01), 0.55, 0.08, 5000, 90_000, 3.9, 4.3)).ToArray();
        return BgSiteTabsTests.Stats(
            heroes: BgSiteTabsTests.List(BgSiteList.Heroes, Heroes("H", 120), mmr: "TOP_50_PERCENT", range: "CURRENT_BATTLEGROUNDS_PATCH"),
            duos: BgSiteTabsTests.List(BgSiteList.Heroes, Heroes("D", 40), mmr: "TOP_50_PERCENT", range: "CURRENT_BATTLEGROUNDS_PATCH"),
            minions: BgSiteTabsTests.List(BgSiteList.Minions, minions),
            trinkets: BgSiteTabsTests.List(BgSiteList.Trinkets, trinkets, mmr: "TOP_1_PERCENT", range: "LAST_7_DAYS"),
            minionStats: stats,
            comps: comps,
            compsAt: new DateTime(2026, 11, 9, 17, 0, 0, DateTimeKind.Utc));
    }
}
