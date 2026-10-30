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

    /// <summary>
    /// The lock button's word stays readable under the mouse in every theme: the plate turns the primary button's hover
    /// fill and the icon and labels stay OnAccent, the pair ThemeContrastTests holds to 4.5:1 in every preset. The tag
    /// buttons' dark shade left "Unlock panels" at 1.31:1 in Tavern.
    /// </summary>
    [Fact]
    public void The_lock_button_keeps_its_word_readable_under_the_mouse()
    {
        var wpf = XDocument.Load(Path.Combine(RepoRoot(), "src/Hearthpulse.App/Overlay/OverlayWindow.xaml")).Root!;
        var style = wpf.Descendants().Single(e => e.Name.LocalName == "Style" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "LockButton"));
        var hover = style.Descendants().Single(e => e.Name.LocalName == "Trigger" && (string?)e.Attribute("Property") == "IsMouseOver").Elements().Single();
        Assert.Equal(("Plate", "Background", "{StaticResource PrimaryFillHover}"),
            ((string?)hover.Attribute("TargetName"), (string?)hover.Attribute("Property"), (string?)hover.Attribute("Value")));

        var ava = XDocument.Load(Path.Combine(RepoRoot(), "src/Hearthpulse.UI/Overlay/OverlayView.axaml")).Root!;
        var pointer = ava.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("Selector") == "Button.lockButton:pointerover /template/ Border#Plate")
            .Elements().Single();
        Assert.Equal(("Background", "{StaticResource PrimaryFillHover}"), ((string?)pointer.Attribute("Property"), (string?)pointer.Attribute("Value")));

        foreach (var view in new[] { wpf, ava })
        {
            var button = view.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "PART_Lock"));
            var texts = button.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(4 - (view == wpf ? 1 : 0), texts.Count);
            Assert.All(texts, t => Assert.Equal("{StaticResource OnAccent}", (string?)t.Attribute("Foreground")));
        }
    }

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
}
