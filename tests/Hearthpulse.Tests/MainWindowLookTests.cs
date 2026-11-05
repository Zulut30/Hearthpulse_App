using System.Globalization;
using System.Text.RegularExpressions;
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
    public void Every_page_title_has_its_glyph_and_the_gold_rule_under_it_in_both_UIs()
    {
        // The window and its page controls (Main/Pages), which hold the pages' trees.
        var wpf = Load("src/Hearthpulse.App/Main/MainWindow.xaml");
        var titles = WithPages(wpf, "src/Hearthpulse.App/Main/Pages", "*.xaml").Where(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Style") == "{StaticResource PageTitle}").ToList();
        Assert.Equal(7, titles.Count);
        Assert.All(titles, t => Assert.Equal("{StaticResource PageRule}", (string?)Next(Head(t))?.Attribute("Style")));
        Assert.All(titles, t => Assert.Contains(Head(t).Descendants(), e => e.Name.LocalName == "GoldGlyph"));
        Assert.DoesNotContain(wpf.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource Display}");

        var ava = Load("src/Hearthpulse.UI/Main/MainView.axaml");
        var avaTitles = WithPages(ava, "src/Hearthpulse.UI/Main/Pages", "*.axaml").Where(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Classes") == "display pageTitle").ToList();
        Assert.Equal(7, avaTitles.Count);
        Assert.All(avaTitles, t => Assert.Equal("pageRule", (string?)Next(Head(t))?.Attribute("Classes")));
        Assert.All(avaTitles, t => Assert.Contains(Head(t).Descendants(), e => e.Name.LocalName == "GoldGlyph"));

        // A title stands in a horizontal row after its page's gold glyph (V3 icons I1); the rule follows the row.
        static XElement Head(XElement title) =>
            title.Parent is { Name.LocalName: "StackPanel" } row && (string?)row.Attribute("Orientation") == "Horizontal" ? row : title;
    }

    [Fact]
    public void Meta_tier_rows_line_up_with_their_header_and_hide_the_empty_play_split_together()
    {
        foreach (var path in new[] { "src/Hearthpulse.App/Main/Meta/ConstructedMetaView.xaml", "src/Hearthpulse.UI/Main/Meta/ConstructedMetaView.axaml" })
        {
            var view = Load(path);
            var header = view.Descendants().Single(e => e.Name.LocalName == "Grid" && Named(e) == "MetaHead");
            var template = view.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(X + "Key") == "ArchetypeRowTemplate");
            var row = template.Descendants().First(e => e.Name.LocalName == "Grid");
            Assert.Equal(GridColumns(header), GridColumns(row));

            var headSplit = header.Elements().Single(e => (string?)e.Attribute("Grid.Column") == "6");
            var rowSplit = row.Elements().Single(e => (string?)e.Attribute("Grid.Column") == "6");
            Assert.Contains("MetaHasPlaySplit", Shown(headSplit), StringComparison.Ordinal);
            Assert.Contains("MetaHasPlaySplit", Shown(rowSplit), StringComparison.Ordinal);
            // Fixed widths inside the split, so every row keeps the header's columns and the same height.
            Assert.Equal(headSplit.Elements().Select(e => (string?)e.Attribute("Width")), rowSplit.Elements().Select(e => (string?)e.Attribute("Width")));

            // Both views list the tiers and the flat table from the same row template, and sort from the heads.
            Assert.Equal(2, view.Descendants().Count(e => ((string?)e.Attribute("ItemTemplate"))?.Contains("ArchetypeRowTemplate") == true));
            Assert.Equal(["Name", "Games", "Tier", "WinRate", "Popularity"],
                header.Descendants().Where(e => ((string?)e.Attribute("Command"))?.Contains("SetMetaSort") == true).Select(e => (string?)e.Attribute("CommandParameter")));
        }
    }

    [Fact]
    public void Battlegrounds_hero_tier_rows_line_up_with_their_sortable_heads_in_both_UIs()
    {
        foreach (var path in new[] { "src/Hearthpulse.App/Main/Meta/BattlegroundsTiersView.xaml", "src/Hearthpulse.UI/Main/Meta/BattlegroundsTiersView.axaml" })
        {
            var view = Load(path);
            var card = view.Descendants().Single(e => Named(e) == "HeroTierCard");
            var header = card.Descendants().First(e => e.Name.LocalName == "Grid");
            var row = card.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && ((string?)e.Attribute("DataType") ?? (string?)e.Attribute(X + "DataType"))!.Contains("MetaHeroRow"))
                .Descendants().First(e => e.Name.LocalName == "Grid");
            Assert.Equal(GridColumns(header), GridColumns(row));
            Assert.Equal(["Name", "Placement", "Top4", "First", "Pick", "Games"],
                header.Elements().Where(e => ((string?)e.Attribute("Command"))?.Contains("SetHeroSort") == true).Select(e => (string?)e.Attribute("CommandParameter")));
            Assert.Contains(card.Descendants(), e => (string?)e.Attribute("ItemsSource") == "{Binding HeroTierGroups}");
        }
    }

    [Fact]
    public void Both_main_windows_have_the_same_constructed_tabs_and_meta_views()
    {
        var wpf = WithPages(Load("src/Hearthpulse.App/Main/MainWindow.xaml"), "src/Hearthpulse.App/Main/Pages", "*.xaml").ToList();
        var ava = WithPages(Load("src/Hearthpulse.UI/Main/MainView.axaml"), "src/Hearthpulse.UI/Main/Pages", "*.axaml").ToList();
        foreach (var name in new[] { "TabMyDecks", "TabMetaTiers", "TabMetaDecks", "TabMetaMatchups", "CtMeta", "BgTiers" })
        {
            Assert.Single(wpf, e => Named(e) == name);
            Assert.Single(ava, e => Named(e) == name);
        }
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

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string? Named(XElement e) => (string?)e.Attribute(X + "Name") ?? (string?)e.Attribute("Name");

    private static string Shown(XElement e) => (string?)e.Attribute("Visibility") ?? (string?)e.Attribute("IsVisible") ?? string.Empty;

    /// <summary>A grid's column widths, written as WPF elements or as Avalonia's ColumnDefinitions="40,*" attribute.</summary>
    private static IEnumerable<string?> GridColumns(XElement grid) =>
        (string?)grid.Attribute("ColumnDefinitions") is { } list
            ? list.Split(',').Select(w => (string?)w.Trim())
            : grid.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().Select(c => (string?)c.Attribute("Width"));

    /// <summary>
    /// A style of the Avalonia main view that picks an element by name reaches only the view's own names: an element moved
    /// into a page control (Main/Pages) keeps its name in the page's scope, so such a style is matched by a class instead
    /// ("Grid#PageHistory" silently stopped matching when the History grid moved and the name went to the page tag).
    /// </summary>
    [Fact]
    public void Main_view_styles_by_name_find_names_of_the_view_itself()
    {
        var view = Load("src/Hearthpulse.UI/Main/MainView.axaml");
        var elements = view.Descendants().Where(e => Named(e) is not null).Select(e => (e.Name.LocalName, Named(e)!)).ToHashSet();
        var selected = view.Descendants().Where(e => e.Name.LocalName == "Style")
            .SelectMany(e => System.Text.RegularExpressions.Regex.Matches((string?)e.Attribute("Selector") ?? string.Empty, @"(\w+)#(\w+)"))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

        Assert.NotEmpty(elements);
        Assert.All(selected, pick => Assert.Contains(pick, elements));
    }

    /// <summary>The elements of a main window and of every page control in <paramref name="folder"/>.</summary>
    private static IEnumerable<XElement> WithPages(XElement window, string folder, string pattern) =>
        window.Descendants().Concat(Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, folder), pattern)
            .SelectMany(f => XDocument.Load(f).Root!.Descendants()));

    /// <summary>CC BY 3.0 asks for the author of each glyph: every game-icons.net icon the app draws is credited, with its author, in each notice that ships.</summary>
    [Fact]
    public void Every_game_icons_glyph_is_credited_with_its_author_in_every_notice()
    {
        var data = File.ReadAllText(Path.Combine(RepoFiles.Root, "src/Hearthpulse.Presentation/Infrastructure/GameIconData.cs"));
        var glyphs = Regex.Matches(data, "\"(?<icon>[a-z-]+)\" by (?<author>\\w+) \\(https://game-icons\\.net/1x1/").ToList();
        Assert.Equal(Regex.Matches(data, "public const string ").Count, glyphs.Count);
        foreach (var notice in new[] { "THIRD-PARTY-NOTICES.txt", "installer/windows/notice.txt", "installer/linux/README.txt", "installer/macos/README.txt" })
        {
            var text = File.ReadAllText(Path.Combine(RepoFiles.Root, notice));
            Assert.Contains("game-icons.net", text, StringComparison.Ordinal);
            Assert.Contains("CC BY 3.0", text.Replace("CC-BY-3.0", "CC BY 3.0", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.All(glyphs, g =>
            {
                Assert.Contains($"\"{g.Groups["icon"].Value}\"", text, StringComparison.Ordinal);
                Assert.Contains(g.Groups["author"].Value, text, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    /// <summary>"Background characters" (Settings > General > Theme) writes AppSettings.PageHeroes, and the page hero's art sits in a layer shown only with a hero, in both UIs.</summary>
    [Fact]
    public void Background_characters_switch_and_backdrop_are_bound_in_both_UIs()
    {
        foreach (var (settings, window) in new[]
                 {
                     ("src/Hearthpulse.App/Main/SettingsTabs/GeneralSettings.xaml", "src/Hearthpulse.App/Main/MainWindow.xaml"),
                     ("src/Hearthpulse.UI/Main/SettingsTabs/GeneralSettings.axaml", "src/Hearthpulse.UI/Main/MainView.axaml"),
                 })
        {
            Assert.Contains(Load(settings).Descendants(), e => e.Name.LocalName == "CheckBox" && (string?)e.Attribute("IsChecked") == "{Binding Settings.PageHeroes}");
            var art = Load(window).Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "CardArt.Splash" && a.Value == "{Binding PageHero}"));
            Assert.Contains(art.Ancestors(), a => a.Attributes().Any(x => x.Name.LocalName is "Visibility" or "IsVisible" && x.Value.Contains("Binding PageHero", StringComparison.Ordinal)));
        }
    }

    private static XElement Load(string relative) => XDocument.Load(Path.Combine(RepoFiles.Root, relative)).Root!;
}
