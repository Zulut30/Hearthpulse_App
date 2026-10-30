using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The settings say where panels move during a game: the "Panel layout" card in both main windows carries a visible
/// line about Ctrl and Ctrl+Shift+L, above its Arrange button. Read from the markup, so it runs on every platform.
/// </summary>
public sealed class OverlayMoveMarkupTests
{
    [Theory]
    [InlineData("src/Hearthpulse.App/Main/MainWindow.xaml", "Overlay.Move.Settings")]
    [InlineData("src/Hearthpulse.UI/Main/MainView.axaml", "Overlay.Move.SettingsWindows")]
    public void The_layout_card_names_ctrl_over_a_panel_and_the_layout_shortcut(string file, string key)
    {
        var card = XDocument.Load(Path.Combine(RepoRoot(), file)).Root!.Descendants()
            .Single(e => e.Name.LocalName == "StackPanel" && e.Elements().Any(c => (string?)c.Attribute("Text") == "{i:Loc Settings.Layout.Title}"))
            .Elements().ToList();
        var line = card.FindIndex(e => (string?)e.Attribute("Text") == $"{{i:Loc {key}}}");
        var arrange = card.FindIndex(e => (string?)e.Attribute("Content") == "{i:Loc Settings.Layout.Arrange}");
        Assert.True(line > 0 && line < arrange, $"{file}: line {line}, button {arrange}");
        Assert.Equal("TextBlock", card[line].Name.LocalName);

        foreach (var language in new[] { Loc.English, Loc.Russian })
        {
            var text = Loc.Table(language)[key];
            Assert.Contains("Ctrl+Shift+L", text, StringComparison.Ordinal);
            Assert.Contains("Ctrl ", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The same card carries the "Unlock panels" switch, under the line that mentions it and above the Arrange button,
    /// bound to the setting the overlay's own button flips (and named, so SyncOverlaySwitch can refresh it).
    /// </summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Main/MainWindow.xaml", "Overlay.Move.Settings", "x:Name")]
    [InlineData("src/Hearthpulse.UI/Main/MainView.axaml", "Overlay.Move.SettingsWindows", "Name")]
    public void The_layout_card_has_the_unlock_switch_under_its_line(string file, string key, string name)
    {
        var card = XDocument.Load(Path.Combine(RepoRoot(), file)).Root!.Descendants()
            .Single(e => e.Name.LocalName == "StackPanel" && e.Elements().Any(c => (string?)c.Attribute("Text") == "{i:Loc Settings.Layout.Title}"))
            .Elements().ToList();
        var line = card.FindIndex(e => (string?)e.Attribute("Text") == $"{{i:Loc {key}}}");
        var toggle = card.FindIndex(e => (string?)e.Attribute("Content") == "{i:Loc Settings.Layout.Unlocked}");
        var arrange = card.FindIndex(e => (string?)e.Attribute("Content") == "{i:Loc Settings.Layout.Arrange}");
        Assert.True(line < toggle && toggle < arrange, $"{file}: line {line}, switch {toggle}, button {arrange}");
        Assert.Equal("CheckBox", card[toggle].Name.LocalName);
        Assert.Equal("{Binding OverlayPanelsUnlocked}", (string?)card[toggle].Attribute("IsChecked"));
        Assert.Equal("PanelLockSetting", card[toggle].Attributes().Single(a => a.Name.LocalName == name.Split(':')[^1]).Value);

        foreach (var language in new[] { Loc.English, Loc.Russian })
        {
            Assert.False(string.IsNullOrWhiteSpace(Loc.Table(language)["Settings.Layout.Unlocked"]));
            Assert.False(string.IsNullOrWhiteSpace(Loc.Table(language)["Settings.Layout.UnlockedCaption"]));
        }
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
