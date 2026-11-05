using Hearthpulse.Core.Constructed;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// The site's Constructed tier floors in one place (Q6 N2): tier letters, the win-rate colour sign of the deck pages and
/// My Stats, and the overlay's meta tier by the Wilson lower bound all read them.
/// </summary>
public sealed class MetaTiersTests
{
    [Theory]
    [InlineData(0.60, "S")]
    [InlineData(0.53, "S")]
    [InlineData(0.5299, "A")]
    [InlineData(0.51, "A")]
    [InlineData(0.49, "B")]
    [InlineData(0.47, "C")]
    [InlineData(0.4699, "D")]
    public void A_win_rate_takes_the_tier_of_the_highest_floor_it_reaches(double winRate, string tier) =>
        Assert.Equal(tier, MetaTiers.Of(winRate));

    [Theory]
    [InlineData(0.55, 1)]
    [InlineData(0.53, 1)]
    [InlineData(0.5299, 0)]
    [InlineData(0.50, 0)]
    [InlineData(0.4701, 0)]
    [InlineData(0.47, -1)]
    [InlineData(0.40, -1)]
    public void The_win_rate_sign_is_plus_from_53_and_minus_at_47_and_below(double winRate, int sign) =>
        Assert.Equal(sign, MetaTiers.WinRateSign(winRate));

    [Fact]
    public void The_overlay_meta_tier_reads_the_same_floors_by_the_lower_bound()
    {
        Assert.Null(OverlayTiers.Meta(0.6, 0));
        // Many games: the lower bound sits close to the rate.
        Assert.Equal("S", OverlayTiers.Meta(0.56, 100_000));
        Assert.Equal("C", OverlayTiers.Meta(0.475, 100_000));
        // Few games pull the same rate a tier or more lower.
        Assert.Equal("D", OverlayTiers.Meta(0.56, 20));
    }
}
