using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The leaderboard badges ("bg-badges") never take the mouse and sit under the hover popup, and their switch stands
/// next to the lobby panel's in both settings pages. Read from the markup, so it runs on every platform.
/// </summary>
public sealed class BgBadgesMarkupTests
{
    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/OverlayWindow.xaml", "src/Hearthpulse.App/Overlay/BgBadgesLayer.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/OverlayView.axaml", "src/Hearthpulse.UI/Overlay/BgBadgesLayer.axaml")]
    public void The_badge_layer_lets_every_click_through_and_draws_under_the_hover_popup(string overlay, string layer)
    {
        var markup = XDocument.Load(Path.Combine(RepoRoot(), layer)).Root!;
        Assert.Equal("False", (string?)markup.Attribute("IsHitTestVisible"));
        Assert.DoesNotContain(markup.DescendantsAndSelf().SelectMany(e => e.Attributes()), a => a.Name.LocalName.Contains("Interactive", StringComparison.Ordinal));

        var elements = XDocument.Load(Path.Combine(RepoRoot(), overlay)).Root!.Descendants().ToList();
        var badges = Assert.Single(elements, e => e.Name.LocalName == "BgBadgesLayer");
        // Not a panel: it is not moved or armed with Ctrl, it follows the leaderboard calibration.
        Assert.DoesNotContain(badges.Ancestors(), e => e.Name.LocalName == "MovablePanel");
        var popup = Assert.Single(elements, e => e.Name.LocalName == "Canvas" && NameOf(e) == "LobbyLayer");
        Assert.True(elements.IndexOf(badges) < elements.IndexOf(popup), "the hover popup must draw over the badges");
        Assert.Same(popup.Parent, badges.Parent);
        // The bottom layer of the surface: the tavern bar's tier list, the counters and the pick rows draw over it.
        Assert.Equal("Surface", NameOf(badges.Parent!));
        Assert.Same(badges, badges.Parent!.Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("src/Hearthpulse.App/Main/MainWindow.xaml")]
    [InlineData("src/Hearthpulse.UI/Main/MainView.axaml")]
    public void The_switch_follows_the_lobby_panels_and_is_on_by_default(string file)
    {
        var switches = XDocument.Load(Path.Combine(RepoRoot(), file)).Root!.Descendants().Where(e => e.Name.LocalName == "CheckBox").ToList();
        var lobby = switches.FindIndex(e => (string?)e.Attribute("IsChecked") == "{Binding ShowBgLobbyPanel}");
        var badges = switches.FindIndex(e => (string?)e.Attribute("IsChecked") == "{Binding ShowBgLeaderboardBadges}");
        Assert.True(lobby >= 0 && badges == lobby + 1, $"{file}: lobby {lobby}, badges {badges}");
        Assert.Equal("{i:Loc Settings.Overlay.BgBadges}", (string?)switches[badges].Attribute("Content"));
        Assert.Contains("{i:Loc Settings.Overlay.BgBadgesTip}", switches[badges].Attributes().Select(a => a.Value));

        Assert.True(new AppSettings().ShowBgLeaderboardBadges);
        foreach (var language in new[] { Loc.English, Loc.Russian })
        {
            Assert.False(string.IsNullOrWhiteSpace(Loc.Table(language)["Settings.Overlay.BgBadges"]));
            Assert.False(string.IsNullOrWhiteSpace(Loc.Table(language)["Settings.Overlay.BgBadgesTip"]));
        }
    }

    private static string? NameOf(XElement element) =>
        (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? (string?)element.Attribute("Name");

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
