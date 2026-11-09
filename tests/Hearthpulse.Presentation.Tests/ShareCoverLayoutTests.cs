using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Hearthpulse.App.Main.Decks;
using Hearthpulse.App.Main.Share;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF share pictures (TASK-2026-11-09-deck-cover): the deck cover's two columns start at the field's top on one line
/// and end inside it, a short deck stays a compact block, every name of a column takes one size that fits it; the list
/// keeps 28-high rows; and the live layout and period buttons switch back and forth with their windows open.
/// </summary>
public sealed class ShareCoverLayoutTests
{
    private const string LongName = "Вихлепых, король контрабандистов";

    [Theory]
    [InlineData(30)]
    [InlineData(21)]
    [InlineData(15)]
    public void Wpf_deck_cover_columns_start_at_the_top_and_names_share_a_size_that_fits(int count) => WpfThread.RunThemed(() =>
    {
        var share = new ShareViewModel(ShareKind.Deck, Deck(count), "Deck", "file", new AppSettings(), null) { Cover = true };
        var window = Open(share);
        try
        {
            var cover = Assert.IsType<DeckCoverView>(window.Card);
            var card = share.Deck!;
            var columns = Descendants(cover).OfType<ItemsControl>().Where(c => c.ItemsSource is IReadOnlyList<DeckListRow>).ToList();
            Assert.Equal(2, columns.Count);
            var tops = columns.Select(c => Container(c, 0).TranslatePoint(default, cover).Y).ToList();
            Assert.All(tops, top => Assert.InRange(top, 31.5, 33));
            Assert.Equal(tops[0], tops[1], 0.5);
            var last = Container(columns[0], card.ColumnRows - 1);
            var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), cover).Y;
            Assert.InRange(bottom, 32 + card.CoverListHeight - 8, 32 + card.CoverListHeight + 0.5);
            Assert.True(bottom <= 540 - 32 + 0.5, $"bottom {bottom}");

            foreach (var column in columns)
            {
                var names = Descendants(column).OfType<TextBlock>().Where(t => ShareTextFit.GetMode(t) == ShareTextFit.Line).ToList();
                Assert.Single(names.Select(n => n.FontSize).Distinct());
                Assert.All(names, n => Assert.True(Width(n) <= n.ActualWidth + 1, $"{n.Text}: {Width(n)} > {n.ActualWidth} at {n.FontSize}"));
                Assert.True(names[0].FontSize >= DeckPictureCard.NameMinFontSize);
            }

            Assert.True(Descendants(columns[0]).OfType<TextBlock>().First(t => t.Text == LongName).FontSize < 12);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Wpf_cover_height_grows_rows_only_while_the_field_has_room()
    {
        Assert.Equal(4 * 38, Deck(8).CoverListHeight);
        Assert.Equal(6 * 38, Deck(12).CoverListHeight);
        Assert.Equal(15 * 31, Deck(30).CoverListHeight);
        Assert.Equal(13 * 36, Deck(26).CoverListHeight);
        var forty = Deck(40);
        Assert.Equal(600, forty.CoverListHeight);
        Assert.Equal(520 * 600 / 476.0, forty.CoverListWidth, 3);
        Assert.Equal(9, forty.CoverNameMinFontSize * forty.CoverScale, 3);
        Assert.Equal(10, Deck(21).CoverNameMinFontSize);
    }

    [Fact]
    public void Wpf_deck_list_keeps_28_high_rows() => WpfThread.RunThemed(() =>
    {
        var share = new ShareViewModel(ShareKind.Deck, Deck(21), "Deck", "file", new AppSettings(), null);
        var window = Open(share);
        try
        {
            var list = Assert.IsType<DeckListView>(window.Card);
            var rows = Descendants(list).OfType<ItemsControl>().Single(c => c.ItemsSource is IReadOnlyList<DeckListRow>);
            Assert.All(Enumerable.Range(0, 21), i => Assert.Equal(DeckPictureCard.RowPitch, Container(rows, i).ActualHeight, 0.5));
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The buttons without GroupName still exclude each other and drive their models, clicked back and forth with two share
    /// windows open and a third closed, and the period buttons of the timestamps window likewise. GroupName went as the
    /// likely cause of a stack overflow in the render scenes; this test is the behaviour check, not a proof of that cause.
    /// </summary>
    [Fact]
    public void Wpf_layout_and_period_buttons_switch_back_and_forth() => WpfThread.RunThemed(() =>
    {
        var settings = new AppSettings();
        var closed = Open(new ShareViewModel(ShareKind.Deck, Deck(15), "Deck", "file", settings, null));
        Click(Named<RadioButton>(closed, "LayoutCover"));
        closed.Close();
        var first = new ShareViewModel(ShareKind.Deck, Deck(21), "Deck", "file", settings, null);
        var second = new ShareViewModel(ShareKind.Deck, Deck(15), "Deck", "file", settings, null);
        var a = Open(first);
        var b = Open(second);
        var stamps = new TimestampsViewModel([], settings, id => id, DateTime.Now);
        var c = new TimestampsWindow(stamps) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        c.Show();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                foreach (var (window, model) in new[] { (a, first), (b, second) })
                {
                    Click(Named<RadioButton>(window, "LayoutCover"));
                    Assert.True(model.Cover);
                    Assert.False(Named<RadioButton>(window, "LayoutList").IsChecked);
                    Assert.IsType<DeckCoverView>(window.Card);
                    Click(Named<RadioButton>(window, "LayoutList"));
                    Assert.True(model.List);
                    Assert.False(Named<RadioButton>(window, "LayoutCover").IsChecked);
                }

                foreach (var name in new[] { "RangeToday", "RangeYesterday", "RangeSession" })
                {
                    Click(Named<RadioButton>(c, name));
                    var buttons = new[] { "RangeSession", "RangeToday", "RangeYesterday" }.Select(n => Named<RadioButton>(c, n)).ToList();
                    Assert.Equal([name], buttons.Where(r => r.IsChecked == true).Select(r => r.Name));
                }
            }

            Assert.True(stamps.RangeSession);
            Assert.True(first.List && second.List);
        }
        finally
        {
            a.Close();
            b.Close();
            c.Close();
        }
    });

    /// <summary>A deck of <paramref name="count"/> rows, the long name second in the first column.</summary>
    private static DeckPictureCard Deck(int count)
    {
        var rows = Enumerable.Range(0, count)
            .Select(i => new DeckListRow($"C{i}", i == 1 ? LongName : $"Карта {i}", i, i == 1 ? "LEGENDARY" : "COMMON", i == 1 ? "★" : "2"))
            .ToList();
        var half = (count + 1) / 2;
        return new DeckPictureCard("Воин", "Воин", "HERO_01", "30 карт", "CODE", rows, rows.Take(half).ToList(), rows.Skip(half).ToList(), [new ManaCurveBar("1", 1, 1)]);
    }

    private static ShareWindow Open(ShareViewModel model)
    {
        var window = new ShareWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>A click as the user makes it: through the button's automation peer.</summary>
    private static void Click(RadioButton button)
    {
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(button).GetPattern(PatternInterface.SelectionItem)).Select();
        (Window.GetWindow(button) ?? throw new InvalidOperationException("no window")).UpdateLayout();
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        Descendants(window).OfType<T>().Single(e => e.Name == name);

    private static FrameworkElement Container(ItemsControl items, int index) => (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(index);

    private static double Width(TextBlock text) =>
        new FormattedText(text.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
