using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Tests;

/// <summary>
/// The value rules behind both UIs' converters (shared presentation, <c>DisplayRules.cs</c>): visibility, the theme brush a
/// tier, rarity, result or sign takes, and share bar widths.
/// </summary>
public sealed class DisplayRulesTests
{
    [Fact]
    public void Truthy_is_false_only_for_nothing_false_zero_and_empty()
    {
        Assert.False(DisplayRules.Truthy(null));
        Assert.False(DisplayRules.Truthy(false));
        Assert.False(DisplayRules.Truthy(0));
        Assert.False(DisplayRules.Truthy(0.0));
        Assert.False(DisplayRules.Truthy(string.Empty));
        Assert.False(DisplayRules.Truthy(Array.Empty<int>()));
        Assert.True(DisplayRules.Truthy(true));
        Assert.True(DisplayRules.Truthy(-1));
        Assert.True(DisplayRules.Truthy(0.5));
        Assert.True(DisplayRules.Truthy("x"));
        Assert.True(DisplayRules.Truthy(new List<int> { 1 }));
        // Anything else that is there counts, a zero long included (only int and double are compared with zero).
        Assert.True(DisplayRules.Truthy(0L));
        Assert.True(DisplayRules.Truthy(new object()));
    }

    [Theory]
    [InlineData("S", "TierS")]
    [InlineData("A", "TierA")]
    [InlineData("B", "TierB")]
    [InlineData("C", "TierC")]
    [InlineData("D", "TierD")]
    [InlineData("E", "TierD")]
    [InlineData("COMMON", "RarityCommon")]
    [InlineData("FREE", "RarityCommon")]
    [InlineData("RARE", "RarityRare")]
    [InlineData("EPIC", "RarityEpic")]
    [InlineData("LEGENDARY", "RarityLegendary")]
    [InlineData("Win", "Positive")]
    [InlineData("Won", "Positive")]
    [InlineData("CORE", "Positive")]
    [InlineData("Loss", "Negative")]
    [InlineData("Lost", "Negative")]
    [InlineData("ADDON", "Info")]
    [InlineData("Tie", "Muted")]
    [InlineData("s", "Muted")]
    [InlineData(null, "Muted")]
    public void Tiers_rarities_and_results_take_their_brush(string? value, string key) =>
        Assert.Equal(key, DisplayRules.BrushKey(value));

    [Theory]
    [InlineData(1, "4", "Positive")]
    [InlineData(4, "4", "Positive")]
    [InlineData(5, "4", "Negative")]
    [InlineData(0, "4", "Muted")]
    [InlineData(3, null, "Positive")]
    [InlineData(-2, null, "Negative")]
    [InlineData(0, null, "Muted")]
    [InlineData(0, "text", "Text")]
    [InlineData(2, "text", "Positive")]
    public void Placements_are_cut_at_the_top_N_and_other_numbers_go_by_their_sign(int value, string? parameter, string key) =>
        Assert.Equal(key, DisplayRules.SignKey(value, parameter));

    [Fact]
    public void Signs_of_other_numbers_and_of_no_number()
    {
        Assert.Equal("Positive", DisplayRules.SignKey(0.4, null));
        Assert.Equal("Negative", DisplayRules.SignKey(-0.1, "4"));
        Assert.Equal("Muted", DisplayRules.SignKey(null, null));
        Assert.Equal("Text", DisplayRules.SignKey(null, "text"));
    }

    [Theory]
    [InlineData(0.25, "200", 50)]
    [InlineData(1.5, "200", 200)]
    [InlineData(-1.0, "200", 0)]
    [InlineData(0.5, null, 50)]
    [InlineData(0.5, "wide", 50)]
    public void A_share_is_clamped_to_0_1_of_the_full_width(double value, string? parameter, double width) =>
        Assert.Equal(width, DisplayRules.FractionWidth(value, parameter), 6);

    [Fact]
    public void Something_that_is_not_a_number_has_no_width() => Assert.Equal(0, DisplayRules.FractionWidth(new object(), "200"));
}
