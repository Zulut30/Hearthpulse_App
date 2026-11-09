using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Avalonia list panel of the long main-window lists (the twin of the WPF PageRowsPanelTests): it builds the rows
/// near what the page shows and a screen more at background priority, while the page keeps the height and row
/// positions a StackPanel would give it; a list that changes in place keeps the rows that stay. And its first user,
/// the meta tier list of the Standard page, in the main view.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class PageRowsPanelAvaloniaTests
{
    private const double RowHeight = 20;
    private const double HeaderHeight = 300;

    [Fact]
    public void Rows_near_the_viewport_come_first_the_rest_follow_and_the_page_keeps_its_height() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var items = Enumerable.Range(0, 100).Select(i => $"row {i}").ToList();
        var rows = List(items, new PageRowsPanel());
        var page = Page(rows, out var window);
        var reference = Page(List(items, new StackPanel()), out var referenceWindow);
        try
        {
            // The layout alone, no background work yet: header 300 + 100 rows of 20, the same extent as the StackPanel's,
            // with only the rows the 400 px page shows (five) and the spare ones built.
            window.UpdateLayout();
            referenceWindow.UpdateLayout();
            var panel = Assert.IsType<PageRowsPanel>(rows.ItemsPanelRoot);
            Assert.Equal(reference.Extent.Height, page.Extent.Height);
            Assert.Equal(HeaderHeight + (100 * RowHeight), page.Extent.Height);
            Assert.InRange(panel.Children.Count, 5, 7);

            // A screen ahead follows at background priority; rows nobody scrolled to stay unbuilt.
            Settle(window);
            Assert.InRange(panel.Children.Count, 8, 15);
            Assert.Null(rows.ContainerFromIndex(99));

            // At the end of the page its last rows are there, in their StackPanel places; the rows built on the way stay.
            page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
            Settle(window);
            foreach (var index in new[] { 0, 99, 90 })
            {
                var row = Assert.IsType<ContentPresenter>(rows.ContainerFromIndex(index));
                Assert.Equal(index * RowHeight, row.TranslatePoint(default, panel)!.Value.Y);
                Assert.Equal($"row {index}", row.Content);
            }

            var last = rows.ContainerFromIndex(99)!;
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), page)!.Value.Y;
            Assert.InRange(bottom, RowHeight, page.Viewport.Height + 0.5);
            // The 400 px the page now shows are all rows (twenty), with a screen of rows above them; nothing in between.
            Assert.InRange(panel.Children.Count, 40, 55);
            Assert.Null(rows.ContainerFromIndex(40));
            Assert.Equal(reference.Extent.Height, page.Extent.Height);

            // A new list starts over: its rows only, and the height of its rows.
            rows.ItemsSource = items.Take(10).ToList();
            Settle(window);
            Assert.Equal(HeaderHeight + (10 * RowHeight), page.Extent.Height);
            Assert.InRange(panel.Children.Count, 1, 10);
        }
        finally
        {
            window.Close();
            referenceWindow.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void A_whole_page_shot_builds_every_row_and_leaves_the_page_where_it_was() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var rows = List(Enumerable.Range(0, 100).Select(i => $"row {i}").ToList(), new PageRowsPanel());
        var page = Page(rows, out var window);
        try
        {
            window.UpdateLayout();
            Assert.Null(rows.ContainerFromIndex(99));

            PageRowsPanel.BuildAllRows(page);

            Assert.All(Enumerable.Range(0, 100), i => Assert.NotNull(rows.ContainerFromIndex(i)));
            Assert.Equal(99 * RowHeight, rows.ContainerFromIndex(99)!.Bounds.Y);
            Assert.Equal(0, page.Offset.Y);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void A_list_changed_in_place_keeps_the_rows_that_stay() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var items = new ObservableCollection<string>(Enumerable.Range(0, 100).Select(i => $"row {i}"));
        var rows = List(items, new PageRowsPanel());
        var page = Page(rows, out var window);
        try
        {
            Settle(window);
            var panel = Assert.IsType<PageRowsPanel>(rows.ItemsPanelRoot);
            var first = rows.ContainerFromIndex(0)!;
            var second = rows.ContainerFromIndex(1)!;
            var third = rows.ContainerFromIndex(2)!;
            var text = Assert.IsType<TextBlock>(Assert.IsType<ContentPresenter>(second).Child);

            // Another item in the same place: the row and the controls of its template stay, the text is the new one.
            items[1] = "changed";
            Settle(window);
            Assert.Same(second, rows.ContainerFromIndex(1));
            Assert.Same(text, ((ContentPresenter)second).Child);
            Assert.Equal("changed", text.Text);
            Assert.Same(first, rows.ContainerFromIndex(0));

            // A row inserted above moves the built ones down a place, a removed one brings them back up.
            items.Insert(0, "new");
            Settle(window);
            Assert.Same(first, rows.ContainerFromIndex(1));
            Assert.Same(second, rows.ContainerFromIndex(2));
            Assert.Equal(2 * RowHeight, second.Bounds.Y);
            Assert.Equal("new", Assert.IsType<ContentPresenter>(rows.ContainerFromIndex(0)).Content);
            Assert.Equal(HeaderHeight + (101 * RowHeight), page.Extent.Height);

            items.RemoveAt(1);
            Settle(window);
            Assert.Same(second, rows.ContainerFromIndex(1));
            Assert.Same(third, rows.ContainerFromIndex(2));
            Assert.DoesNotContain(first, panel.Children);
            Assert.Equal(RowHeight, second.Bounds.Y);

            items.Move(1, 3);
            Settle(window);
            Assert.Equal(["new", "row 2", "row 3", "changed"], Enumerable.Range(0, 4).Select(i => ((ContentPresenter)rows.ContainerFromIndex(i)!).Content));
            Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(i * RowHeight, rows.ContainerFromIndex(i)!.Bounds.Y));

            items.Clear();
            Settle(window);
            Assert.Empty(panel.Children);
            Assert.Equal(0, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>
    /// The meta tier list in a 1280×800 window with 80 archetypes (85 rows with the tiers' heads): fewer than half of the
    /// rows are built, and no more than what the page shows plus a screen above and a screen below; the end of the page
    /// has the last row. The list is laid out before the side card's sections, which come without the player doing
    /// anything and do not move the list; another choice replaces them.
    /// </summary>
    [Fact]
    public void The_meta_tier_list_builds_the_rows_near_the_viewport_and_the_side_card_after_the_list() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var db = TestDataFolder.Scratch("hp-page-rows") + ".db";
        var model = new MainViewModel(new HistoryStore(db), new AppSettings(), TestDataFolder.App);
        var view = new MainView { DataContext = model };
        var window = new Window { Width = 1280, Height = 800 };
        // The headless platform has no Inter: the app swaps UiFont at startup the same way (HearthpulseApp).
        window.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        window.Content = view;
        try
        {
            window.Show();
            view.ShowPage("Standard");
            view.ShowConstructedTab("Tiers");
            model.SetConstructedMeta(Meta(80));

            // The layout alone: the list has its rows, the side card its name and summary, its sections are not built.
            window.UpdateLayout();
            var list = view.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "MetaTierList");
            var panel = Assert.IsType<PageRowsPanel>(list.ItemsPanelRoot);
            var page = Assert.IsType<ScrollViewer>(list.FindAncestorOfType<ScrollViewer>());
            var card = view.GetVisualDescendants().OfType<Border>().Single(c => c.Name == "ArchetypeCard");
            var sections = view.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Name == "ArchetypeSections");
            Assert.Equal(85, list.ItemCount);
            Assert.InRange(panel.Children.Count, 5, 30);
            Assert.NotNull(model.SelectedArchetype);
            Assert.False(model.ArchetypeDetailReady);
            Assert.False(sections.IsVisible);
            Assert.Empty(MulliganCards(card));
            Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == model.SelectedArchetypeTitle && t.IsEffectivelyVisible);
            var place = (list.TranslatePoint(default, view)!.Value, list.Bounds.Width);

            // The dispatcher idle: the sections of the chosen archetype are there, and the list stands where it stood.
            Settle(window);
            Assert.True(model.ArchetypeDetailReady);
            Assert.True(sections.IsEffectivelyVisible);
            Assert.Equal(model.Mulligan.Select(m => m.CardId), MulliganCards(card));
            Assert.Equal(["CARD_" + model.SelectedArchetype!.Id], MulliganCards(card));
            Assert.Equal(place, (list.TranslatePoint(default, view)!.Value, list.Bounds.Width));

            // Rows: every row has the same height, a tier's head as an archetype; fewer than half of the list is built,
            // and no more than the rows the page shows and two screens of them.
            var step = panel.Children[0].Bounds.Height;
            Assert.All(panel.Children, row => Assert.Equal(step, row.Bounds.Height));
            Assert.Equal(48, step);

            // A row's plate stands 8 px out of the list on each side (so its columns stay under the head), and the list
            // does not cut it: the tier colour down a head's left edge is in that overhang.
            Assert.False(list.ClipToBounds);
            var plate = panel.Children[1].GetVisualDescendants().OfType<Button>().First();
            Assert.Equal((-8, list.Bounds.Width + 16, 46), (plate.Bounds.X, plate.Bounds.Width, plate.Bounds.Height));
            var shown = panel.Children.Count(row => row.TranslatePoint(default, page) is { } at && at.Y + step > 0 && at.Y < page.Viewport.Height);
            var screen = (int)Math.Ceiling(page.Viewport.Height / step);
            Assert.InRange(shown, 3, screen);
            Assert.True(panel.Children.Count < list.ItemCount / 2, $"{panel.Children.Count} of {list.ItemCount} rows built");
            Assert.True(panel.Children.Count <= shown + (2 * screen), $"{panel.Children.Count} rows built, {shown} shown, {screen} to a screen");
            Assert.Null(list.ContainerFromIndex(84));

            // The end of the page: the last row is built and in view.
            page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
            Settle(window);
            var last = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(84));
            var top = last.TranslatePoint(default, page)!.Value.Y;
            Assert.InRange(top, 0, page.Viewport.Height - step);
            Assert.True(panel.Children.Count < list.ItemCount, "rows nobody scrolled to stay unbuilt");

            // Another choice: its sections replace the first one's.
            var other = model.MetaArchetypes[^1];
            model.SelectArchetype.Execute(other);
            Settle(window);
            Assert.Equal(["CARD_" + other.Id], MulliganCards(card));

            // Another order, then a class nobody plays, then every class again: the list is changed in place, and every
            // built row shows the item that now stands in its place, a head as a plate and an archetype as a button.
            var source = list.ItemsSource;
            model.SetMetaSort.Execute("Name");
            Settle(window);
            AssertRowsShowTheirItems();
            model.SetMetaClass.Execute("PRIEST");
            Settle(window);
            Assert.Equal(5, list.ItemCount);
            Assert.Equal(5, panel.Children.Count);
            AssertRowsShowTheirItems();
            model.SetMetaClass.Execute(string.Empty);
            Settle(window);
            Assert.Equal(85, list.ItemCount);
            AssertRowsShowTheirItems();
            Assert.Same(source, list.ItemsSource);

            void AssertRowsShowTheirItems()
            {
                Assert.NotEmpty(panel.Children);
                foreach (var row in panel.Children.Cast<ContentPresenter>())
                {
                    var item = model.MetaTierRows[list.IndexFromContainer(row)];
                    Assert.Same(item, row.Content);
                    Assert.Same(item, row.Child!.DataContext);
                    Assert.Equal(item is TierHeadRow ? typeof(Border) : typeof(Button), row.Child.GetType());
                    Assert.Equal(list.IndexFromContainer(row) * step, row.Bounds.Y);
                }
            }
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
            TestDataFolder.TryDelete(db);
        }

        static List<string> MulliganCards(Control card) =>
            card.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.DataContext is MulliganRow && b.Classes.Contains("row"))
                .Select(b => ((MulliganRow)b.DataContext!).CardId).ToList();
    });

    /// <summary>Archetypes over every tier (win rates from 60 % down to 40 %), each with one kept card named after it.</summary>
    private static ConstructedMeta Meta(int count)
    {
        var archetypes = Enumerable.Range(1, count)
            .Select(i => (Id: i, WinRate: 0.60 - (0.20 * (i - 1) / (count - 1))))
            .Select(a => new MetaArchetype(a.Id, "Archetype " + a.Id, "Archetype " + a.Id, "MAGE", 1000 + a.Id, (int)((1000 + a.Id) * a.WinRate), a.WinRate, 1.0 / count,
                0, 0, 0, 0, [], [new MetaMulliganCard("CARD_" + a.Id, 900, 0.8, 700, a.WinRate + 0.03)]))
            .ToList();
        return new ConstructedMeta("standard", "legend", "past_week", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), count * 1000, archetypes, []);
    }

    /// <summary>Layout, the background build it queued, and the layout of what that built, until nothing is left to do.</summary>
    internal static void Settle(Window window)
    {
        for (var pass = 0; pass < 50; pass++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
    }

    private static ItemsControl List(IEnumerable<string> items, Panel panel) => new()
    {
        ItemsSource = items,
        ItemsPanel = new FuncTemplate<Panel?>(() => panel),
        ItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock { Height = RowHeight, [!TextBlock.TextProperty] = new Avalonia.Data.Binding() }, supportsRecycling: true),
    };

    /// <summary>A 400 px high scrolling page: a 300 px header, then the list.</summary>
    private static ScrollViewer Page(ItemsControl list, out Window window)
    {
        var page = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = HeaderHeight }, list } } };
        window = new Window { Content = page, Width = 500, Height = 400 };
        window.Show();
        return page;
    }

    private static Styles Theme()
    {
        var theme = new Styles
        {
            new FluentTheme(),
            new StyleInclude(new Uri("avares://Hearthpulse.UI/")) { Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml") },
        };
        Application.Current!.Styles.Add(theme);
        return theme;
    }
}
