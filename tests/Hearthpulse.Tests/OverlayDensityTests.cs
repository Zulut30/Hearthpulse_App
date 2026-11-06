using System.Text;
using System.Text.RegularExpressions;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// The overlay density presets (D5): one choice sets the overlay's size and the compact rows of every panel that has
/// them, keeps each panel's own opacity, and is read back from the settings; a size set by hand is no preset.
/// </summary>
public sealed class OverlayDensityTests
{
    [Fact]
    public void A_fresh_overlay_is_normal()
    {
        Assert.Equal(OverlayDensityPreset.Normal, OverlayDensity.Of(new AppSettings()));
    }

    [Theory]
    [InlineData(OverlayDensityPreset.Compact, 0.85, true)]
    [InlineData(OverlayDensityPreset.Normal, 1.0, false)]
    [InlineData(OverlayDensityPreset.Large, 1.2, false)]
    public void A_preset_sets_the_size_and_every_compact_panel_and_reads_back(OverlayDensityPreset preset, double scale, bool compact)
    {
        var settings = new AppSettings();
        settings.PanelLooks["opponent"] = new PanelLook { Opacity = 0.6 };

        OverlayDensity.Apply(settings, preset);

        Assert.Equal(scale, settings.OverlayScale, 3);
        Assert.All(OverlayDensity.CompactPanels, id => Assert.Equal(compact, settings.PanelLooks.GetValueOrDefault(id)?.Compact ?? false));
        // A panel's own opacity stays; a panel left with nothing set keeps no entry.
        Assert.Equal(0.6, settings.PanelLooks["opponent"].Opacity, 3);
        Assert.Equal(compact, settings.PanelLooks.ContainsKey("deck"));
        Assert.Equal(preset, OverlayDensity.Of(settings));
    }

    [Fact]
    public void A_size_or_a_panel_set_by_hand_is_no_preset()
    {
        var settings = new AppSettings();
        OverlayDensity.Apply(settings, OverlayDensityPreset.Compact);
        settings.PanelLooks["graveyard"].Compact = false;
        Assert.Null(OverlayDensity.Of(settings));

        settings = new AppSettings { OverlayScale = 1.1 };
        Assert.Null(OverlayDensity.Of(settings));
    }

    /// <summary>The Settings preview's sample game: the deck panel lists the sample cards the database has, the rest left out.</summary>
    [Fact]
    public void The_preview_shows_the_sample_cards_the_database_has()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
            [
              {"id": "CS2_024", "dbfId": 662, "name": "Frostbolt", "cost": 2, "rarity": "COMMON"},
              {"id": "EX1_559", "dbfId": 1080, "name": "Archmage Antonidas", "cost": 7, "rarity": "LEGENDARY"}
            ]
            """)));
        Loc.Instance.SetLanguage(Loc.English);

        var preview = OverlayPreview.Build(cards, new AppSettings());

        Assert.Equal(["Frostbolt", "Archmage Antonidas"], preview.Deck.Select(t => t.Name));
        Assert.True(preview.Deck[1].Legendary);
        Assert.Equal(Loc.T("Settings.Overlay.PreviewDeck"), preview.DeckName);
    }

    /// <summary>The panels a preset makes compact are exactly those whose tag offers compact rows, in both overlay views.</summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Overlay/OverlayWindow.xaml")]
    [InlineData("src/Hearthpulse.UI/Overlay/OverlayView.axaml")]
    public void The_compact_panels_are_the_ones_the_overlay_offers(string view)
    {
        var markup = File.ReadAllText(Path.Combine(RepoFiles.Root, view));
        var offered = Regex.Matches(markup, @"<o:MovablePanel\s[^>]*?Id=""([^""]+)""[^>]*?CanCompact=""True""")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal);
        Assert.Equal(OverlayDensity.CompactPanels.Order(StringComparer.Ordinal), offered);
    }
}
