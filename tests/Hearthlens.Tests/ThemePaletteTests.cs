using Hearthlens.Core;
using Xunit;

namespace Hearthlens.Tests;

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
    public void UnknownNameIsACustomTheme()
    {
        var custom = ThemePalette.Resolve("mine", 400, -20, 3);
        Assert.Equal(ThemePalette.CustomName, custom.Name);
        Assert.Equal(40, custom.AccentHue);
        Assert.Equal(340, custom.BaseHue);
        Assert.Equal(1.5, custom.BaseTint);
    }
}
