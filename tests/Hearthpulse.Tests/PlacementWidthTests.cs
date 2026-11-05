using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>Average-place bars of the Battlegrounds lists (Q6 N4): one formula for the four lists that drew them.</summary>
public sealed class PlacementWidthTests
{
    [Theory]
    [InlineData(3.5, 3.5, 5.5, 1.0)]
    [InlineData(5.5, 3.5, 5.5, 0.15)]
    [InlineData(4.5, 3.5, 5.5, 0.575)]
    [InlineData(4.2, 4.2, 4.205, 1.0)]
    [InlineData(4.205, 4.2, 4.205, 1.0)]
    public void The_best_place_is_full_the_worst_fifteen_percent_and_a_tie_full(double place, double best, double worst, double width) =>
        Assert.Equal(width, MainViewModel.PlacementWidth(place, best, worst), 9);
}
