using System.Xml.Linq;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The leaderboard badges ("bg-badges") never take the mouse, sit under the hover popup and say everything in words (no
/// tribe glyphs), and their switch stands next to the lobby panel's in both settings pages. Read from the markup, so it
/// runs on every platform.
/// </summary>
public sealed class BgBadgesMarkupTests
{
    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/OverlayWindow.xaml", "src/Hearthpulse.App/Overlay/BgBadgesLayer.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/OverlayView.axaml", "src/Hearthpulse.UI/Overlay/BgBadgesLayer.axaml")]
    public void The_badge_layer_lets_every_click_through_and_draws_under_the_hover_popup(string overlay, string layer)
    {
        var markup = XDocument.Load(Path.Combine(RepoFiles.Root, layer)).Root!;
        Assert.Equal("False", (string?)markup.Attribute("IsHitTestVisible"));
        Assert.DoesNotContain(markup.DescendantsAndSelf().SelectMany(e => e.Attributes()), a => a.Name.LocalName.Contains("Interactive", StringComparison.Ordinal));

        var elements = XDocument.Load(Path.Combine(RepoFiles.Root, overlay)).Root!.Descendants().ToList();
        var badges = Assert.Single(elements, e => e.Name.LocalName == "BgBadgesLayer");
        // Not a panel: it is not moved or armed with Ctrl, it follows the leaderboard calibration.
        Assert.DoesNotContain(badges.Ancestors(), e => e.Name.LocalName == "MovablePanel");
        Assert.DoesNotContain(elements, e => NameOf(e) == "LobbyLayer");
        var popup = Assert.Single(elements, e => e.Name.LocalName == "MovablePanel" && (string?)e.Attribute("Id") == "lobby-popup");
        Assert.True(elements.IndexOf(badges) < elements.IndexOf(popup), "the hover popup must draw over the badges");
        Assert.Same(popup.Parent, badges.Parent);
        // The bottom layer of the surface: the tavern bar's tier list, the counters and the pick rows draw over it.
        Assert.Equal("Surface", NameOf(badges.Parent!));
        Assert.Same(badges, badges.Parent!.Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)));
    }

    /// <summary>The tribe is a word everywhere it was an icon: the badges, the popup and the "bg-lobby" panel.</summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/BgBadgesLayer.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/BgBadgesLayer.axaml")]
    [InlineData("src/Hearthpulse.App/Overlay/LobbyPopupPanel.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/LobbyPopupPanel.axaml")]
    [InlineData("src/Hearthpulse.App/Overlay/BgLobbyPanel.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/BgLobbyPanel.axaml")]
    public void Tribes_are_written_out_without_glyphs(string file)
    {
        var markup = File.ReadAllText(Path.Combine(RepoFiles.Root, file));
        Assert.DoesNotContain("Glyph", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("TribeCount}", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/Hearthpulse.App/Main/SettingsTabs/OverlaySettings.xaml")]
    [InlineData("src/Hearthpulse.UI/Main/SettingsTabs/OverlaySettings.axaml")]
    public void The_switch_follows_the_lobby_panels_and_is_on_by_default(string file)
    {
        var switches = XDocument.Load(Path.Combine(RepoFiles.Root, file)).Root!.Descendants().Where(e => e.Name.LocalName == "CheckBox").ToList();
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
}
