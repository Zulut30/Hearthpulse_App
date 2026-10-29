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
