using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>
/// Main-window text and layout rules that hold in both UIs: deck names as the player reads them, one percent format,
/// and the page layout read from the XAML (no render: the checks run on the markup).
/// </summary>
public sealed class MainWindowLookTests
{
    [Theory]
    [InlineData("Рыцарь смерти3", "Рыцарь смерти 3")]
    [InlineData("Mage2", "Mage 2")]
    [InlineData("Deck123", "Deck 123")]
    [InlineData("Deck1234", "Deck1234")]
    [InlineData("Zoo 2", "Zoo 2")]
    [InlineData("R2D2", "R2D2")]
    [InlineData("XL7", "XL7")]
    [InlineData("2024", "2024")]
    [InlineData("Aggro Druid", "Aggro Druid")]
    [InlineData("", "")]
    public void Deck_names_get_the_space_Hearthstone_leaves_out_before_a_copy_number(string name, string expected) =>
        Assert.Equal(expected, DeckName.Display(name));

    [Fact]
    public void Percentages_follow_the_number_culture_everywhere()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            var ru = 0.552.ToString("P1", CultureInfo.CurrentCulture);
            Assert.Equal(ru.Replace("55,2", "12,8"), new PoolMechanic("TAUNT", "Taunt", 3, 12.8).PercentText);
            Assert.EndsWith(0.5.ToString("P0", CultureInfo.CurrentCulture), BattlegroundsMeta.BracketLabel(50));

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            Assert.Equal("12.8%", new PoolMechanic("TAUNT", "Taunt", 3, 12.8).PercentText);
            Assert.Equal("top 50%", BattlegroundsMeta.BracketLabel(50));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Every_page_title_has_the_gold_rule_under_it_in_both_UIs()
    {
        var wpf = Load("src/Hearthpulse.App/Main/MainWindow.xaml");
        var titles = wpf.Descendants().Where(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Style") == "{StaticResource PageTitle}").ToList();
        Assert.Equal(6, titles.Count);
        Assert.All(titles, t => Assert.Equal("{StaticResource PageRule}", (string?)Next(t)?.Attribute("Style")));
        Assert.DoesNotContain(wpf.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource Display}");

        var ava = Load("src/Hearthpulse.UI/Main/MainView.axaml");
        var avaTitles = ava.Descendants().Where(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Classes") == "display pageTitle").ToList();
        Assert.Equal(6, avaTitles.Count);
        Assert.All(avaTitles, t => Assert.Equal("pageRule", (string?)Next(t)?.Attribute("Classes")));
    }

    [Fact]
    public void Meta_tier_rows_line_up_with_their_header_and_hide_the_empty_play_split_together()
    {
        var wpf = Load("src/Hearthpulse.App/Main/MainWindow.xaml");
        var list = wpf.Descendants().Single(e => e.Name.LocalName == "ListBox" && (string?)e.Attribute("ItemsSource") == "{Binding MetaArchetypes}");
        var header = (XElement)list.PreviousNode!;
        var row = list.Descendants().First(e => e.Name.LocalName == "DataTemplate").Elements().Single();
        Assert.Equal(Columns(header), Columns(row));

        var headSplit = header.Elements().Single(e => (string?)e.Attribute("Grid.Column") == "4");
        var rowSplit = row.Elements().Single(e => (string?)e.Attribute("Grid.Column") == "4");
        Assert.Contains("MetaHasPlaySplit", (string?)headSplit.Attribute("Visibility"));
        Assert.Contains("MetaHasPlaySplit", (string?)rowSplit.Attribute("Visibility"));
        // Fixed widths inside the split, so every row keeps the header's columns and the same height (PageRows).
        Assert.Equal(headSplit.Elements().Select(e => (string?)e.Attribute("Width")), rowSplit.Elements().Select(e => (string?)e.Attribute("Width")));
    }

    [Fact]
    public void Battlegrounds_hero_table_heads_show_only_over_rows_in_both_UIs()
    {
        foreach (var path in new[] { "src/Hearthpulse.App/Main/Stats/BattlegroundsStatsView.xaml", "src/Hearthpulse.UI/Main/Stats/BattlegroundsStatsView.axaml" })
        {
            var view = Load(path);
            var head = view.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{i:Loc Stats.Bg.Hero}");
            var guarded = head.Ancestors().Any(a => ((string?)a.Attribute("Visibility") ?? (string?)a.Attribute("IsVisible"))?.Contains("BgHeroPerf") == true);
            Assert.True(guarded, path);
            var empty = view.Descendants().Single(e => (string?)e.Attribute("Content") == "{i:Loc Stats.Bg.NoHeroes}");
            Assert.Equal("ContentControl", empty.Name.LocalName);
        }
    }

    private static XElement? Next(XElement element) => element.ElementsAfterSelf().FirstOrDefault();

    private static IEnumerable<string?> Columns(XElement grid) =>
        grid.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().Select(c => (string?)c.Attribute("Width"));

    private static XElement Load(string relative) => XDocument.Load(Path.Combine(RepoRoot(), relative)).Root!;

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Hearthpulse.slnx) not found above the test sources.");
    }
}
