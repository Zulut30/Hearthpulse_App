using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) where texts and markup are enough to tell: names as the game
/// gives them, one wording for one state, the same structure in both overlays. Read from the files, so it runs on every
/// platform.
/// </summary>
public sealed class UxRound2MarkupTests
{
    [Fact]
    public void Excavate_reads_as_Dobycha_and_Discover_stays_Raskopka()
    {
        var ru = Loc.Table(Loc.Russian);
        // Two counters stand side by side: the Russian client calls Excavate "Добыча" and Discover "Раскопка".
        var excavate = ru.Where(p => p.Key.Contains("excavate", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(excavate);
        Assert.All(excavate, p => Assert.DoesNotContain("Раскоп", p.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Добыч", ru["Core.Counter.excavate.Title"], StringComparison.Ordinal);
        Assert.Contains("Добыч", ru["Pool.Mechanic.EXCAVATE"], StringComparison.Ordinal);
        Assert.Contains("Раскоп", ru["Core.Counter.discovers.Title"], StringComparison.Ordinal);
        Assert.Contains("Раскоп", ru["Pool.Mechanic.DISCOVER"], StringComparison.Ordinal);
    }

    /// <summary>What is still on its way, one pattern: "{What} загружается…" / "Loading {what}…".</summary>
    private static readonly string[] LoadingKeys =
    [
        "Main.Bg.CommunityLoading", "Stats.Bg.NoteLoading", "Main.Meta.Loading", "Host.Busy.LoadingCards", "Overlay.Bar.ReadingLobby",
        "Arena.Page.CommunityLoading",
    ];

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Loading_texts_follow_one_pattern(string language)
    {
        var table = Loc.Table(language);
        foreach (var key in LoadingKeys)
        {
            var text = table[key];
            Assert.EndsWith("…", text, StringComparison.Ordinal);
            if (language == Loc.Russian)
            {
                Assert.False(text.StartsWith("Загрузка", StringComparison.Ordinal) || text.StartsWith("Загружаем", StringComparison.Ordinal)
                    || text.StartsWith("Читаю", StringComparison.Ordinal), $"{key}: {text}");
            }
            else
            {
                Assert.True(text.StartsWith("Loading ", StringComparison.Ordinal) || text.StartsWith("Reading ", StringComparison.Ordinal), $"{key}: {text}");
            }
        }

        // The same Battlegrounds community data has one name on the meta page and in My Stats.
        Assert.Equal(table["Main.Bg.CommunityLoading"], table["Stats.Bg.NoteLoading"]);
        // An update is "downloaded" in both places that announce it, and the version stays in the text.
        Assert.Contains("{0}", table["Main.Update.Downloaded"], StringComparison.Ordinal);
        if (language == Loc.Russian)
        {
            Assert.Contains("скачана", table["Main.Update.Downloaded"], StringComparison.Ordinal);
            Assert.Contains("скачана", table["Host.Update.Ready"], StringComparison.Ordinal);
        }

        // Numbers that could not be had are "unavailable", not a load that went wrong.
        var noData = table["Arena.NoData"];
        Assert.Contains(language == Loc.Russian ? "недоступн" : "unavailable", noData, StringComparison.Ordinal);
        Assert.DoesNotContain(language == Loc.Russian ? "не загрузились" : "did not load", noData, StringComparison.Ordinal);
    }

    /// <summary>
    /// The names one panel goes by in the live bar's "⋯" menu, in Settings and over its layout placeholder: the player
    /// looks in Settings for what the menu called it.
    /// </summary>
    private static readonly string[][] PanelNames =
    [
        ["Main.Live.Panel.OpponentDeck", "Settings.Tracker.Opponent"],
        ["Main.Live.Panel.Secrets", "Settings.Overlay.Secrets", "Overlay.Panel.Secrets"],
        ["Main.Live.Panel.BgBar", "Overlay.Panel.BgBar"],
        ["Main.Live.Panel.BgSession", "Settings.Overlay.Session"],
        ["Settings.Tracker.Summary", "Overlay.Panel.Toast"],
        ["Main.Live.Panel.Odds", "Settings.Overlay.Odds", "Overlay.Panel.Odds"],
        ["Main.Live.Panel.Boards", "Settings.Overlay.Boards"],
    ];

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Each_panel_has_one_name_everywhere(string language)
    {
        var table = Loc.Table(language);
        foreach (var keys in PanelNames)
        {
            Assert.All(keys.Skip(1), key => Assert.True(table[keys[0]] == table[key], $"{keys[0]} \"{table[keys[0]]}\" vs {key} \"{table[key]}\""));
        }

        // Inside the Battlegrounds group of Settings the short name stands for the same bar.
        Assert.Contains(table["Settings.Overlay.TopBar"], table["Main.Live.Panel.BgBar"], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/ArenaDraftPanel.xaml", "Visibility")]
    [InlineData("src/Hearthpulse.UI/Overlay/ArenaDraftPanel.axaml", "IsVisible")]
    public void Arena_draft_says_why_numbers_are_missing_right_under_its_header(string file, string visibility)
    {
        var elements = Markup(file).Descendants().ToList();
        var header = elements.First(e => IsHeader(e));
        // The body follows the header: the reason for the dashes is its first line, above the class plate and the lists.
        var body = header.ElementsAfterSelf().First();
        var note = body.Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));
        Assert.Equal("TextBlock", note.Name.LocalName);
        Assert.Equal("{Binding ArenaNoData}", (string?)note.Attribute("Text"));
        Assert.Equal("ArenaNoDataNote", NameOf(note));
        Assert.Single(elements, e => (string?)e.Attribute("Text") == "{Binding ArenaNoData}");
        // Hidden while the numbers are there, as before.
        Assert.StartsWith("{Binding ArenaNoData", (string?)note.Attribute(visibility), StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_rows_keep_text_size_and_one_gem_size_in_both_uis()
    {
        // WPF: the row templates' triggers on the panel's compact flag.
        var setters = Markup("src/Hearthpulse.App/Overlay/OverlayWindow.xaml").Descendants()
            .Where(e => e.Name.LocalName == "Trigger" && (string?)e.Attribute("Property") == "o:MovablePanel.Compact")
            .SelectMany(t => t.Elements().Where(e => e.Name.LocalName == "Setter")).ToList();
        Assert.NotEmpty(setters);
        Assert.DoesNotContain(setters, s => (string?)s.Attribute("Property") == "FontSize");
        var gem = setters.Where(s => (string?)s.Attribute("TargetName") == "Gem").ToList();
        Assert.Equal(4, gem.Count);
        Assert.All(gem, s => Assert.Equal("18", (string?)s.Attribute("Value")));

        // Avalonia: the styles under a compact panel.
        var styles = Markup("src/Hearthpulse.UI/Overlay/OverlayView.axaml").Descendants()
            .Where(e => e.Name.LocalName == "Style" && ((string?)e.Attribute("Selector"))?.Contains("MovablePanel.compact", StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(styles);
        Assert.DoesNotContain(styles.SelectMany(s => s.Elements()), s => (string?)s.Attribute("Property") == "FontSize");
        var gems = styles.Where(s => ((string?)s.Attribute("Selector"))!.Contains("ManaCost", StringComparison.Ordinal))
            .SelectMany(s => s.Elements().Where(e => (string?)e.Attribute("Property") is "Width" or "Height")).ToList();
        Assert.Equal(2, gems.Count);
        Assert.All(gems, s => Assert.Equal("18", (string?)s.Attribute("Value")));
    }

    private const string WpfOverlay = "src/Hearthpulse.App/Overlay/OverlayWindow.xaml";
    private const string AvaOverlay = "src/Hearthpulse.UI/Overlay/OverlayView.axaml";

    /// <summary>
    /// What may sit on the overlay surface without being a <c>MovablePanel</c>, and why: each one follows something in the
    /// game or belongs to layout mode itself. Everything else the player can move (Ctrl, Ctrl+Shift+L) and resize.
    /// </summary>
    private static readonly (string Role, Func<XElement, bool> Is)[] SurfaceExceptions =
    [
        // Stands at the leaderboard portraits and follows their calibration.
        ("leaderboard badges", e => e.Name.LocalName == "BgBadgesLayer"),
        // Follows the hand card or Discover option it is about; the same pool is the movable "card-pool" panel too.
        ("card pool at its card", e => e.Name.LocalName == "Canvas"
            && e.Elements().Any(c => c.Name.LocalName == "CardPoolPanel" && (string?)c.Attribute("Anchored") == "True")),
        // Opens at the hovered leaderboard portrait.
        ("leaderboard hover", e => e.Name.LocalName == "Canvas" && NameOf(e) == "LobbyLayer"),
        // Shows only while the leaderboard is calibrated.
        ("calibration guides", e => e.Name.LocalName == "Canvas" && NameOf(e) == "GuideLayer"),
        // Layout mode's own banner.
        ("layout banner", e => e.Name.LocalName == "Border"
            && e.Attributes().Any(a => a.Name.LocalName is "Visibility" or "IsVisible" && a.Value.StartsWith("{Binding LayoutEditing", StringComparison.Ordinal))),
        // The one-time "hold Ctrl" tip: shown once, never takes the mouse.
        ("move tip", e => e.Name.LocalName == "Border" && NameOf(e) == "MoveTip"),
        // The card render at the pointer or card.
        ("card preview", e => e.Name.LocalName == "CardPreviewHost" && NameOf(e) == "PreviewHost"),
    ];

    [Theory]
    [InlineData(WpfOverlay)]
    [InlineData(AvaOverlay)]
    public void Every_overlay_block_is_a_movable_panel_or_a_listed_exception(string file)
    {
        var surface = Surface(file);
        var roles = new List<string>();
        foreach (var block in surface.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)))
        {
            if (block.Name.LocalName == "MovablePanel")
            {
                continue;
            }

            var exception = SurfaceExceptions.FirstOrDefault(x => x.Is(block));
            Assert.True(exception.Role is not null,
                $"{file}:{((IXmlLineInfo)block).LineNumber}: a {block.Name.LocalName} on the overlay surface is not a MovablePanel; wrap it, or list it with its reason");
            roles.Add(exception.Role);
        }

        // Every exception is still there once (a stale entry would let a new block slip through under its name).
        Assert.Equal(SurfaceExceptions.Select(x => x.Role).Order(), roles.Order());
    }

    [Fact]
    public void Movable_panels_have_ids_and_titles_alike_in_both_uis()
    {
        static List<(string Id, string Title)> Panels(string file)
        {
            var panels = Surface(file).Elements().Where(e => e.Name.LocalName == "MovablePanel")
                .Select(e => (Id: (string?)e.Attribute("Id") ?? string.Empty, Title: (string?)e.Attribute("Title") ?? string.Empty)).ToList();
            Assert.All(panels, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Id), file);
                // The placeholder in layout mode carries a localized name.
                Assert.Matches(@"^\{i:Loc [A-Za-z0-9.]+\}$", p.Title);
                Assert.True(Loc.Has(p.Title[7..^1]), p.Title);
            });
            Assert.Equal(panels.Count, panels.Select(p => p.Id).Distinct().Count());
            return panels;
        }

        var wpf = Panels(WpfOverlay);
        var ava = Panels(AvaOverlay);
        Assert.Equal(26, wpf.Count);
        Assert.Equal(wpf.Order(), ava.Order());
    }

    /// <summary>The overlay panels that are lists under a header (not strips like the Battlegrounds bar or a timer).</summary>
    private static readonly string[] ListPanels =
        ["deck", "opponent", "opponent-archetype", "secrets", "ladder-session", "card-pool", "arena-draft", "lobby", "bg-lobby"];

    /// <summary>
    /// One header for every list panel: the gold band first in the card, then an optional glyph, the title and a muted line
    /// in the shared header styles, which alone size them; a figure (a number in the display font) may stand at the right.
    /// The same parts in both UIs.
    /// </summary>
    [Fact]
    public void List_panels_share_one_header_in_both_uis()
    {
        var wpf = Headers(WpfOverlay, ".xaml");
        var ava = Headers(AvaOverlay, ".axaml");
        Assert.Equal(wpf, ava);

        static SortedDictionary<string, string> Headers(string overlay, string extension)
        {
            var panels = Surface(overlay).Elements().Where(e => e.Name.LocalName == "MovablePanel").ToDictionary(e => (string)e.Attribute("Id")!);
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ListPanels)
            {
                var content = panels[id].Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));
                if (content.Name.LocalName is not ("Border" or "StackPanel" or "Grid"))
                {
                    // An extracted panel: its own file next to the overlay.
                    content = Markup(Path.Combine(Path.GetDirectoryName(overlay)!, content.Name.LocalName + extension));
                }

                var card = content.DescendantsAndSelf().First(e => e.Name.LocalName == "Border"
                    && ((string?)e.Attribute("Style") == "{StaticResource OverlayCard}" || Classes(e).Contains("overlayCard")));
                var header = card.Descendants().First(e => e.Name.LocalName == "Border");
                Assert.True(IsHeader(header), $"{overlay} {id}: the first border in the card is not the header band");

                var parts = new List<string>();
                foreach (var text in header.Descendants().Where(e => e.Name.LocalName == "TextBlock"
                             && !e.Ancestors().TakeWhile(a => a != header).Any(a => a.Name.LocalName.Contains('.', StringComparison.Ordinal) && a.Name.LocalName.Contains("ToolTip", StringComparison.Ordinal))))
                {
                    var part = Part(text);
                    Assert.True(part is not null, $"{overlay} {id}: \"{(string?)text.Attribute("Text")}\" is not a header glyph, title, note or figure");
                    Assert.True(part == "figure" || text.Attribute("FontSize") is null, $"{overlay} {id}: \"{(string?)text.Attribute("Text")}\" sizes itself");
                    parts.Add(part!);
                }

                Assert.Contains("title", parts);
                Assert.Contains("note", parts);
                headers[id] = string.Join(",", parts.Distinct().Order(StringComparer.Ordinal));
            }

            return headers;
        }

        static string? Part(XElement text) =>
            (string?)text.Attribute("Style") switch
            {
                "{StaticResource OverlayHeaderIcon}" => "icon",
                "{StaticResource OverlayHeaderTitle}" => "title",
                "{StaticResource OverlayHeaderNote}" => "note",
                _ when Classes(text).Contains("headerIcon") => "icon",
                _ when Classes(text).Contains("headerTitle") => "title",
                _ when Classes(text).Contains("headerNote") => "note",
                _ when ((string?)text.Attribute("FontFamily"))?.Contains("DisplayFont", StringComparison.Ordinal) == true => "figure",
                _ => null,
            };
    }

    [Fact]
    public void Header_notes_and_strip_labels_are_one_style_of_11()
    {
        var wpf = Markup("src/Hearthpulse.App/Overlay/OverlayLook.xaml").Elements()
            .Single(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "OverlayHeaderNote");
        Assert.Equal("11", FontSize(wpf));
        var ava = Markup("src/Hearthpulse.UI/Overlay/OverlayLook.axaml").Elements().Single(e => (string?)e.Attribute("Selector") == "TextBlock.headerNote");
        Assert.Equal("11", FontSize(ava));

        // The strips without a header (the Battlegrounds bar's turn, the session, the turn timers) label their figures alike.
        foreach (var (file, text) in new[]
                 {
                     (WpfOverlay, "{i:Loc Overlay.Bar.Turn}"), (WpfOverlay, "{i:Loc Overlay.Session.Title}"),
                     ("src/Hearthpulse.App/Overlay/TurnTimerPanel.xaml", "{Binding Label}"),
                     (AvaOverlay, "{i:Loc Overlay.Bar.Turn}"), (AvaOverlay, "{i:Loc Overlay.Session.Title}"),
                     ("src/Hearthpulse.UI/Overlay/TurnTimerPanel.axaml", "{Binding Label}"),
                 })
        {
            var label = Markup(file).Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == text);
            Assert.True((string?)label.Attribute("Style") == "{StaticResource OverlayHeaderNote}" || Classes(label).Contains("headerNote"), $"{file}: {text}");
            Assert.Null(label.Attribute("FontSize"));
        }

        static string? FontSize(XElement style) =>
            (string?)style.Elements().Single(s => (string?)s.Attribute("Property") == "FontSize").Attribute("Value");
    }

    /// <summary>The card pool browser stands below the top bar; its card ends above the screen's bottom edge, 16 clear.</summary>
    [Theory]
    [InlineData(WpfOverlay, "src/Hearthpulse.App/Overlay/CardPoolBrowser.xaml")]
    [InlineData(AvaOverlay, "src/Hearthpulse.UI/Overlay/CardPoolBrowser.axaml")]
    public void Card_pool_browser_fits_the_screen_from_its_default_spot(string overlay, string browser)
    {
        var panel = Surface(overlay).Elements().Single(e => e.Name.LocalName == "MovablePanel" && (string?)e.Attribute("Id") == "card-pool-browser");
        Assert.Equal("Top", (string?)panel.Attribute("VerticalAlignment"));
        var top = double.Parse(((string)panel.Attribute("Margin")!).Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
        var card = Markup(browser).Elements().First(e => e.Name.LocalName == "Border");
        var height = double.Parse((string)card.Attribute("Height")!, System.Globalization.CultureInfo.InvariantCulture);
        // Taller, it ran 132 below the 1080 surface: WPF cut its bottom rows, Avalonia its header and close button.
        Assert.True(top + height <= 1080 - 16, $"{browser}: {top} + {height}");
    }

    private static HashSet<string> Classes(XElement element) =>
        ((string?)element.Attribute("Classes") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static XElement Surface(string file) =>
        XDocument.Load(Path.Combine(RepoRoot(), file), LoadOptions.SetLineInfo).Root!.Descendants().Single(e => NameOf(e) == "Surface");

    private static bool IsHeader(XElement element) =>
        element.Name.LocalName == "Border"
        && ((string?)element.Attribute("Style") == "{StaticResource OverlayHeader}" || ((string?)element.Attribute("Classes"))?.Split(' ').Contains("overlayHeader") == true);

    private static XElement Markup(string file) => XDocument.Load(Path.Combine(RepoRoot(), file)).Root!;

    private static string? NameOf(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? (string?)element.Attribute("Name");

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
