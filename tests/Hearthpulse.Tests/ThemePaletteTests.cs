using Hearthpulse.Core;
using Xunit;

namespace Hearthpulse.Tests;

public sealed class ThemePaletteTests
{
    private static ThemeSpec Preset(string name) => ThemePalette.Resolve(name, 0, 0, 1);

    [Fact]
    public void DefaultThemeKeepsEveryColour()
    {
        var tavern = Preset("Tavern");
        Assert.True(ThemePalette.IsDefault(tavern));
        Assert.Equal(0xFFE9B949u, ThemePalette.Map(0xFFE9B949u, tavern));
        Assert.Equal(0xFF140D08u, ThemePalette.Map(0xFF140D08u, tavern));
    }

    [Theory]
    [InlineData(0xFF8CCF63u)] // positive green
    [InlineData(0xFFEC6A52u)] // negative red
    [InlineData(0xFF6FB8F2u)] // info blue
    [InlineData(0x80000000u)] // shadow
    [InlineData(0xFFFFFFFFu)]
    public void MeaningfulAndNeutralColoursNeverChange(uint argb)
    {
        Assert.Equal(argb, ThemePalette.Map(argb, Preset("Frost")));
    }

    [Fact]
    public void FrostTurnsGoldBlueAndKeepsAlpha()
    {
        var mapped = ThemePalette.Map(0x99E9B949u, Preset("Frost"));
        Assert.Equal(0x99u, mapped >> 24);
        var r = (byte)(mapped >> 16);
        var b = (byte)mapped;
        Assert.True(b > r, $"expected a blue accent, got {mapped:X8}");
    }

    [Fact]
    public void ShadowDrainsTheWoodButKeepsTheGold()
    {
        var shadow = Preset("Shadow");
        Assert.Equal(0xFFE9B949u, ThemePalette.Map(0xFFE9B949u, shadow));
        var wood = ThemePalette.Map(0xFF3A2817u, shadow);
        var spread = Math.Max((byte)(wood >> 16), (byte)wood) - Math.Min((byte)(wood >> 16), (byte)wood);
        Assert.True(spread < 12, $"expected a near grey, got {wood:X8}");
    }

    [Fact]
    public void TheSecondAccentTakesEachPresetsOwnHue()
    {
        Assert.Equal(0xFF4FC2A8u, ThemePalette.Map(0xFF4FC2A8u, Preset("Tavern")));
        foreach (var preset in ThemePalette.Presets.Skip(1))
        {
            var mapped = ThemePalette.Map(0x994FC2A8u, preset);
            Assert.Equal(0x99u, mapped >> 24);
            Assert.InRange(HueDistance(Hue(mapped), preset.SecondaryHue), 0, 4);
        }
    }

    [Fact]
    public void AccentLiftLightensArcaneAccentsAndSurfaceLiftDarkensShadowWood()
    {
        var plain = new ThemeSpec("x", 280, 262, 0.8);
        Assert.True(Lightness(ThemePalette.Map(0xFFE9B949u, Preset("Arcane"))) > Lightness(ThemePalette.Map(0xFFE9B949u, plain)));
        var flat = new ThemeSpec("y", 42, 28, 0.12);
        Assert.True(Lightness(ThemePalette.Map(0xFF3A2817u, Preset("Shadow"))) < Lightness(ThemePalette.Map(0xFF3A2817u, flat)));
    }

    [Fact]
    public void ACustomThemeBorrowsTheNearestMotifAndSpacesItsAccents()
    {
        var custom = ThemePalette.Resolve("mine", 200, 214, 0.8);
        Assert.Equal("Frost", custom.Motif);
        Assert.Equal(326, custom.SecondaryHue);
        Assert.False(ThemePalette.IsDefault(ThemePalette.Resolve("mine", 42, 28, 1.0) with { Motif = "Ember" }));
        Assert.True(ThemePalette.IsDefault(ThemePalette.Resolve("mine", 42, 28, 1.0)));
    }

    private static double Hue(uint argb) => Hsl(argb).H;

    private static double Lightness(uint argb) => Hsl(argb).L;

    private static double HueDistance(double a, double b) => Math.Min(((a - b) % 360 + 360) % 360, ((b - a) % 360 + 360) % 360);

    private static (double H, double L) Hsl(uint argb)
    {
        double r = (byte)(argb >> 16) / 255.0, g = (byte)(argb >> 8) / 255.0, b = (byte)argb / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
        return ((h + 360) % 360, (max + min) / 2);
    }

    [Fact]
    public void UnknownNameIsACustomTheme()
    {
        var custom = ThemePalette.Resolve("mine", 400, -20, 3);
        Assert.Equal(ThemePalette.CustomName, custom.Name);
        Assert.Equal(40, custom.AccentHue);
        Assert.Equal(340, custom.BaseHue);
        Assert.Equal(1.5, custom.BaseTint);
    }
}
