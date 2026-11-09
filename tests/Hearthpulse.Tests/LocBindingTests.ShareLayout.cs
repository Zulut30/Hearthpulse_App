using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main.Decks;
using Hearthpulse.UI.Main.Share;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The 16:9 deck cover (TASK-2026-11-09-deck-cover): 21 rows split 11 / 10 fill the card's field from its top margin,
    /// both columns start on the same line and end inside the field, and a long name gets smaller instead of an ellipsis.
    /// </summary>
    [Fact]
    public void Share_deck_cover_fills_the_field_with_even_columns() => Headless.Run(DeckCoverLayout);

    [Fact]
    public void Share_text_fit_steps_down_to_the_width_and_stops_at_the_floor()
    {
        Assert.Equal(13, ShareTextFit.Fit(13, 9, 200, s => s * 10));
        Assert.Equal(10, ShareTextFit.Fit(13, 9, 100, s => s * 10));
        Assert.Equal(9, ShareTextFit.Fit(13, 9, 10, s => s * 10));
    }

    [Fact]
    public void Share_sample_decks_have_21_30_and_15_rows_of_known_cards()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < 40; i++)
        {
            json.Append(i == 0 ? string.Empty : ",")
                .Append($$"""{"id":"X_{{i:00}}","dbfId":{{1000 + i}},"name":"Card {{new string('a', i)}}","cost":1,"type":"MINION","cardClass":"NEUTRAL","set":"BE","collectible":true}""");
        }

        json.Append(",{\"id\":\"END_020\",\"dbfId\":2000,\"name\":\"Known\",\"cost\":1,\"type\":\"SPELL\",\"cardClass\":\"WARRIOR\",\"set\":\"END\",\"collectible\":true}]");
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(json.ToString())));
        var decks = ShareSampleDecks.All(cards).ToList();
        Assert.Equal([21, 30, 15], decks.Select(d => d.Rows));
        Assert.Equal(["END_020"], decks[0].Deck.Keys);
        Assert.Equal(30, decks[1].Deck.Count);
        Assert.Contains("X_39", decks[1].Deck.Keys);
        Assert.DoesNotContain("X_00", decks[1].Deck.Keys);
    }

    private static void DeckCoverLayout()
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        var app = Application.Current!;
        var theme = Theme();
        var rows = Enumerable.Range(0, 21)
            .Select(i => new DeckListRow($"C{i}", i == 11 ? "Кровавый боец Ло'гош" : $"Карта {i}", i, i == 11 ? "LEGENDARY" : "COMMON", i == 11 ? "★" : "2"))
            .ToList();
        var card = new DeckPictureCard("Воин", "Воин", "HERO_01", "30 карт", "CODE", rows, rows.Take(11).ToList(), rows.Skip(11).ToList(), [new ManaCurveBar("1", 1, 1)]);
        Assert.Equal(11, card.ColumnRows);
        Assert.Equal(476, card.CoverListHeight);
        Assert.Equal(600, (card with { FirstColumn = rows.Take(20).ToList() }).CoverListHeight);
        var share = new ShareViewModel(ShareKind.Deck, card, "Deck", "file", new AppSettings(), null) { Cover = true };
        var window = new ShareWindow(share);
        try
        {
            Show(window);
            Dispatcher.UIThread.RunJobs();
            var cover = Assert.IsType<DeckCoverView>(window.Card);
            var columns = cover.GetVisualDescendants().OfType<ItemsControl>().Where(c => c.ItemsSource is IReadOnlyList<DeckListRow>).ToList();
            Assert.Equal(2, columns.Count);
            var firsts = columns.Select(c => c.ContainerFromIndex(0)!).ToList();
            var tops = firsts.Select(f => f.TranslatePoint(default, cover)!.Value.Y).ToList();
            Assert.All(tops, top => Assert.InRange(top, 31.5, 36));
            Assert.Equal(tops[0], tops[1], 0.5);
            var last = columns[0].ContainerFromIndex(10)!;
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), cover)!.Value.Y;
            Assert.InRange(bottom, 540 - 32 - 8, 540 - 32);

            var longName = cover.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.StartsWith("Кровавый", StringComparison.Ordinal) == true);
            Assert.Equal(TextTrimming.CharacterEllipsis, longName.TextTrimming);
            Assert.True(longName.FontSize < 12, $"font {longName.FontSize}");
            var width = new FormattedText(longName.Text!, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(longName.FontFamily, longName.FontStyle, longName.FontWeight), longName.FontSize, null).WidthIncludingTrailingWhitespace;
            Assert.True(width <= longName.Bounds.Width + 1, $"{width} > {longName.Bounds.Width}");
        }
        finally
        {
            window.Close();
            Loc.Instance.SetLanguage(Loc.English);
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
