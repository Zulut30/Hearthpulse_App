using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// Layout mode shows every panel at once, each as a placeholder at its default spot. The placeholders are laid out here
/// from both overlay files (alignment, margin, minimum width, placeholder alignment) with an estimated label width, and
/// must not lie on top of each other in either language: the last render had "Choice" over "Tavern hints" and "Secrets"
/// over "Opponent's hand". No fonts or windows are needed, so it runs on every platform.
/// </summary>
public sealed class OverlayLayoutTests
{
    private const double SurfaceWidth = 1920;
    private const double SurfaceHeight = 1080;

    // The placeholder template: MinWidth 200, MinHeight 36, 16,8 padding around a 13 px title (and an 11 px hint line),
    // a dashed frame 4 units outside it (two frames may touch: 3 units are counted).
    private const double MinWidth = 200;
    private const double MinHeight = 36;
    private const double HintedHeight = 50;
    private const double FramePad = 3;

    public static TheoryData<string, string> Surfaces() => new()
    {
        { "src/Hearthpulse.App/Overlay/OverlayWindow.xaml", Loc.English },
        { "src/Hearthpulse.App/Overlay/OverlayWindow.xaml", Loc.Russian },
        { "src/Hearthpulse.UI/Overlay/OverlayView.axaml", Loc.English },
        { "src/Hearthpulse.UI/Overlay/OverlayView.axaml", Loc.Russian },
    };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Layout_placeholders_do_not_overlap(string file, string language)
    {
        var placeholders = Panels(file).Select(p => (p.Id, Rect: Placeholder(p, Loc.Table(language)))).ToList();
        var overlaps = new List<string>();
        for (var i = 0; i < placeholders.Count; i++)
        {
            for (var j = i + 1; j < placeholders.Count; j++)
            {
                if (Intersects(placeholders[i].Rect, placeholders[j].Rect))
                {
                    overlaps.Add($"{placeholders[i].Id} {placeholders[i].Rect} / {placeholders[j].Id} {placeholders[j].Rect}");
                }
            }
        }

        Assert.True(overlaps.Count == 0, string.Join(Environment.NewLine, overlaps));
    }

    [Fact]
    public void Both_overlays_place_their_panels_and_placeholders_alike()
    {
        var wpf = Panels("src/Hearthpulse.App/Overlay/OverlayWindow.xaml").ToDictionary(p => p.Id);
        var ava = Panels("src/Hearthpulse.UI/Overlay/OverlayView.axaml").ToDictionary(p => p.Id);

        Assert.Equal(wpf.Keys.Order(StringComparer.Ordinal), ava.Keys.Order(StringComparer.Ordinal));
        Assert.All(wpf.Values, p => Assert.Equal(p, ava[p.Id]));
    }

    /// <summary>
    /// Panels declared later draw on top. The tavern bar's tier list opens down from the top-left corner, so a panel
    /// that can lie under it is declared before the bar: the open list covers it and takes the mouse.
    /// </summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/OverlayWindow.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/OverlayView.axaml")]
    public void The_tavern_bar_tier_list_draws_over_the_lobby_panel(string file)
    {
        var ids = Panels(file).Select(p => p.Id).ToList();
        Assert.InRange(ids.IndexOf("bg-lobby"), 0, ids.IndexOf("bgbar") - 1);
    }

    /// <summary>
    /// The lobby panel at its default spot, full (eight players, two lines of tribes), keeps clear of the leaderboard
    /// (at the left edge of the game's 4:3 area), the tavern bar with its open tier list and the Battlegrounds counters
    /// on 16:9, 16:10 and 4:3 screens. The surface is 1080 units high; its width follows the screen.
    /// </summary>
    [Theory]
    [InlineData(1920.0)]
    [InlineData(1728.0)]
    [InlineData(1440.0)]
    public void Lobby_panel_default_spot_keeps_clear_of_the_leaderboard_bar_and_counters(double width)
    {
        var panelWidth = Assert.Single(new[] { "src/Hearthpulse.App/Overlay/BgLobbyPanel.xaml", "src/Hearthpulse.UI/Overlay/BgLobbyPanel.axaml" }
            .Select(f => double.Parse(XDocument.Load(Path.Combine(RepoRoot(), f)).Root!.Descendants().First(e => e.Name.LocalName == "Border").Attribute("Width")!.Value,
                CultureInfo.InvariantCulture))
            .Distinct());
        var leaderboard = ((width - (SurfaceHeight * 4 / 3)) / 2, AppSettings.DefaultLeaderboardTop * SurfaceHeight,
            (AppSettings.DefaultLeaderboardWidth + 0.012) * SurfaceHeight, AppSettings.DefaultLeaderboardSlotHeight * 8 * SurfaceHeight);
        foreach (var file in new[] { "src/Hearthpulse.App/Overlay/OverlayWindow.xaml", "src/Hearthpulse.UI/Overlay/OverlayView.axaml" })
        {
            var panels = Panels(file).ToDictionary(p => p.Id);
            var lobby = Box(panels["bg-lobby"], panelWidth, 300, width);
            Assert.False(Intersects(lobby, leaderboard), $"{file} {width}: {lobby} / leaderboard {leaderboard}");
            // The bar with its tier list open (MaxWidth 880, about 850 down); the counters' chips above their bottom margin.
            Assert.False(Intersects(lobby, Box(panels["bgbar"], 880, 850, width)), $"{file} {width}: bgbar");
            Assert.False(Intersects(lobby, Box(panels["bg-counters"], 600, 200, width)), $"{file} {width}: bg-counters");
        }
    }

    private static (double X, double Y, double W, double H) Box(Panel panel, double width, double height, double surfaceWidth) =>
        (Place(panel.Horizontal, panel.Margin.Left, panel.Margin.Right, width, surfaceWidth),
         Place(panel.Vertical, panel.Margin.Top, panel.Margin.Bottom, height, SurfaceHeight), width, height);

    private sealed record Panel(string Id, string TitleKey, bool Hinted, string Horizontal, string Vertical, (double Left, double Top, double Right, double Bottom) Margin, double MinWidth, string PlaceholderAlignment);

    private static List<Panel> Panels(string file)
    {
        var root = XDocument.Load(Path.Combine(RepoRoot(), file)).Root!;
        var surface = root.Descendants().Single(e => e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "Surface"
                                                      || e.Attribute("Name")?.Value == "Surface");
        return surface.Elements()
            .Where(e => e.Name.LocalName == "MovablePanel")
            .Select(e => new Panel(
                e.Attribute("Id")!.Value,
                LocKey(e.Attribute("Title")!.Value),
                e.Attribute("Hint") is not null,
                e.Attribute("HorizontalAlignment")?.Value ?? "Stretch",
                e.Attribute("VerticalAlignment")?.Value ?? "Stretch",
                Margin(e.Attribute("Margin")?.Value),
                e.Attribute("MinWidth")?.Value switch
                {
                    null => 0,
                    var v when v.Contains("HandPanelWidth", StringComparison.Ordinal) => Hearthpulse.UI.Overlay.OverlayViewModel.HandPanelWidth,
                    var v => double.Parse(v, CultureInfo.InvariantCulture),
                },
                e.Attribute("PlaceholderAlignment")?.Value ?? "Stretch"))
            .ToList();
    }

    private static (double, double, double, double) Margin(string? value)
    {
        var parts = (value ?? "0").Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 4 ? (parts[0], parts[1], parts[2], parts[3]) : (parts[0], parts[0], parts[0], parts[0]);
    }

    private static string LocKey(string title) => Regex.Match(title, @"\{i:Loc\s+([A-Za-z0-9_.-]+)\s*\}").Groups[1].Value;

    /// <summary>The placeholder's frame on the surface, from the panel's default alignment and margin.</summary>
    private static (double X, double Y, double W, double H) Placeholder(Panel panel, IReadOnlyDictionary<string, string> texts)
    {
        // A 13 px SemiBold title runs about 7.4 units a character; the label never gets narrower than the template's minimum.
        var label = Math.Max(MinWidth, (texts[panel.TitleKey].Length * 7.4) + 34);
        var height = panel.Hinted ? HintedHeight : MinHeight;
        var box = Math.Max(panel.MinWidth, label);
        var x = Place(panel.Horizontal, panel.Margin.Left, panel.Margin.Right, box, SurfaceWidth);
        var y = Place(panel.Vertical, panel.Margin.Top, panel.Margin.Bottom, height, SurfaceHeight);
        var labelX = panel.PlaceholderAlignment switch
        {
            "Left" => x,
            "Right" => x + box - label,
            "Center" => x + ((box - label) / 2),
            _ => x,
        };
        var width = panel.PlaceholderAlignment == "Stretch" ? box : label;
        return (labelX - FramePad, y - FramePad, width + (2 * FramePad), height + (2 * FramePad));
    }

    private static double Place(string alignment, double before, double after, double size, double room) => alignment switch
    {
        "Left" or "Top" => before,
        "Right" or "Bottom" => room - after - size,
        _ => before + ((room - before - after - size) / 2),
    };

    private static bool Intersects((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
