using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Decks;
using Hearthpulse.App.Main.Share;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF deck cover (TASK-2026-11-09-deck-cover): 21 rows split 11 / 10 fill the card's field from its top margin, both
/// columns start on one line, a long name gets smaller instead of an ellipsis, and the layout switches back and forth.
/// </summary>
public sealed class ShareCoverLayoutTests
{
    [Fact]
    public void Wpf_deck_cover_fills_the_field_with_even_columns()
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
            throw new InvalidOperationException("The deck cover check failed", failure);
        }
    }

    [Fact]
    public void Text_fit_steps_down_to_the_width_and_stops_at_the_floor()
    {
        Assert.Equal(13, ShareTextFit.Fit(13, 9, 200, s => s * 10));
        Assert.Equal(10, ShareTextFit.Fit(13, 9, 100, s => s * 10));
        Assert.Equal(9, ShareTextFit.Fit(13, 9, 10, s => s * 10));
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Frost", 0, 0, 1)));
        var settings = new AppSettings();
        var rows = Enumerable.Range(0, 21)
            .Select(i => new DeckListRow($"C{i}", i == 11 ? "Вихлепых, король контрабандистов" : $"Карта {i}", i, i == 11 ? "LEGENDARY" : "COMMON", i == 11 ? "★" : "2"))
            .ToList();
        var deck = new DeckPictureCard("Воин", "Воин", "HERO_01", "30 карт", "CODE", rows, rows.Take(11).ToList(), rows.Skip(11).ToList(), [new ManaCurveBar("1", 1, 1)]);

        var share = new ShareViewModel(ShareKind.Deck, deck, "Deck", "file", settings, null);
        var window = Open(share);
        share.Cover = true;
        window.UpdateLayout();
        share.Cover = false;
        share.Cover = true;
        window.UpdateLayout();
        var cover = Assert.IsType<DeckCoverView>(window.Card);

        var columns = Descendants(cover).OfType<ItemsControl>().Where(c => c.ItemsSource is IReadOnlyList<DeckListRow>).ToList();
        Assert.Equal(2, columns.Count);
        var tops = columns.Select(c => ((FrameworkElement)c.ItemContainerGenerator.ContainerFromIndex(0)).TranslatePoint(default, cover).Y).ToList();
        Assert.All(tops, top => Assert.InRange(top, 31.5, 36));
        Assert.Equal(tops[0], tops[1], 0.5);
        var last = (FrameworkElement)columns[0].ItemContainerGenerator.ContainerFromIndex(10);
        Assert.InRange(last.TranslatePoint(new Point(0, last.ActualHeight), cover).Y, 540 - 32 - 8, 540 - 32);

        var longName = Descendants(cover).OfType<TextBlock>().Single(t => t.Text.StartsWith("Вихлепых", StringComparison.Ordinal));
        Assert.True(longName.FontSize < 12, $"font {longName.FontSize}");
        var width = new FormattedText(longName.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(longName.FontFamily, longName.FontStyle, longName.FontWeight, longName.FontStretch), longName.FontSize, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
        Assert.True(width <= longName.ActualWidth + 1, $"{width} > {longName.ActualWidth}");
        window.Close();
    }

    private static ShareWindow Open(ShareViewModel model)
    {
        var window = new ShareWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        return window;
    }

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
