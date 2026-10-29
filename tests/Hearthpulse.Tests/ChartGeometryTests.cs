using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

/// <summary>
/// The geometry both UIs' charts draw with (shared presentation, <c>Controls/ChartGeometry.cs</c>): where the line chart's
/// points, game numbers, hover card and latest-value tag go, in pixels of a 200 × 120 control.
/// </summary>
public sealed class ChartGeometryTests
{
    private sealed record Value(double ChartValue, string ChartLabel) : IChartValue;

    private static IReadOnlyList<IChartValue> Values(params double[] values) =>
        values.Select(v => (IChartValue)new Value(v, v.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();

    // Six pixels a character: "10" and "20" are 12 wide.
    private static double Width(string label) => label.Length * 6;

    [Fact]
    public void Points_spread_across_the_plot_with_room_for_the_guide_labels_and_some_above_and_below()
    {
        var plot = ChartGeometry.Line(Values(10, 20, 15), 200, 120, Width);

        // Left: the wider guide label (12) rounded up, plus 16.
        Assert.Equal(28, plot.Left);
        Assert.Equal(0, plot.Lowest);
        Assert.Equal(1, plot.Highest);
        Assert.Equal([28, 107, 186], plot.At.Select(p => p.X));

        // 12 % of the range (1.2) above the highest and below the lowest; the plot is 120 - 16 - 26 = 78 high.
        Assert.Equal(16 + (78 * (1 - (1.2 / 12.4))), plot.At[0].Y, 9);
        Assert.Equal(16 + (78 * (1 - (11.2 / 12.4))), plot.At[1].Y, 9);
        Assert.Equal(55, plot.At[2].Y, 9);
    }

    [Fact]
    public void One_point_sits_in_the_middle_and_a_flat_line_in_the_middle_height()
    {
        var single = ChartGeometry.Line(Values(5), 200, 120, Width);
        Assert.Equal((single.Left + 200 - ChartGeometry.LineRight) / 2, single.At[0].X);

        // No range: 0.4 % of the value (at least 1) above and below, so the line sits halfway.
        var flat = ChartGeometry.Line(Values(1000, 1000, 1000), 200, 120, Width);
        Assert.All(flat.At, p => Assert.Equal(55, p.Y, 9));
        Assert.Equal(flat.Lowest, flat.Highest);
    }

    [Fact]
    public void No_points_measure_no_label()
    {
        var plot = ChartGeometry.Line([], 200, 120, _ => throw new InvalidOperationException("measured"));

        Assert.Empty(plot.At);
        Assert.Equal(10, plot.Left);
        Assert.Equal(LinePlot.Empty.Left, plot.Left);
    }

    [Fact]
    public void The_pointer_picks_the_nearest_point_across_the_first_of_two_equally_near()
    {
        (double X, double Y)[] at = [(28, 0), (107, 0), (186, 0)];

        Assert.Equal(1, ChartGeometry.Nearest(at, 100));
        Assert.Equal(0, ChartGeometry.Nearest(at, -50));
        Assert.Equal(2, ChartGeometry.Nearest(at, 500));
        Assert.Equal(0, ChartGeometry.Nearest(at, 67.5));
        Assert.Equal(0, ChartGeometry.Nearest([], 10));
    }

    [Fact]
    public void The_hover_card_goes_right_of_its_point_unless_it_would_not_fit()
    {
        Assert.Equal((68, 30), ChartGeometry.Card((50, 60), 100, 60, 200, 120));
        // Too far right: left of the point, but never past the left edge.
        Assert.Equal((32, 30), ChartGeometry.Card((150, 60), 100, 60, 200, 120));
        Assert.Equal((0, 30), ChartGeometry.Card((90, 60), 150, 60, 200, 120));
        // Up and down it stays inside: at the top, at the bottom, or at the top when it is taller than the control.
        Assert.Equal(0, ChartGeometry.Card((50, 10), 100, 60, 200, 120).Y);
        Assert.Equal(60, ChartGeometry.Card((50, 115), 100, 60, 200, 120).Y);
        Assert.Equal(0, ChartGeometry.Card((50, 60), 100, 200, 200, 120).Y);
    }

    [Fact]
    public void Game_numbers_go_under_the_first_middle_and_last_point()
    {
        Assert.Empty(ChartGeometry.Ticks(0));
        Assert.Equal([0], ChartGeometry.Ticks(1));
        Assert.Equal([0, 1], ChartGeometry.Ticks(2));
        Assert.Equal([0, 1, 2], ChartGeometry.Ticks(3));
        Assert.Equal([0, 4, 9], ChartGeometry.Ticks(10));

        // Centred under the tick, but not left of the plot's edge by more than half its width or past the right edge.
        Assert.Equal(95, ChartGeometry.TickLabelX(100, 10, 28, 200));
        Assert.Equal(23, ChartGeometry.TickLabelX(28, 10, 28, 200));
        Assert.Equal(190, ChartGeometry.TickLabelX(199.5, 10, 28, 200));
    }

    [Fact]
    public void Points_are_dots_only_while_they_stay_nine_pixels_apart()
    {
        Assert.True(ChartGeometry.DotsFit([]));
        Assert.True(ChartGeometry.DotsFit([(5, 5)]));
        Assert.True(ChartGeometry.DotsFit([(0, 0), (9, 0), (18, 0)]));
        Assert.False(ChartGeometry.DotsFit([(0, 0), (8, 0), (17, 0)]));
    }

    [Fact]
    public void The_latest_tag_sits_over_its_point_or_under_it_at_the_top()
    {
        // Text 30 × 16: the tag is 44 × 20, centred on the point and 14 above it.
        Assert.Equal((78, 46, 44, 20), ChartGeometry.Tag((100, 80), 30, 16, 28, ChartGeometry.LineRight, 200));
        // No room above: 14 under the point.
        Assert.Equal((78, 34, 44, 20), ChartGeometry.Tag((100, 20), 30, 16, 28, ChartGeometry.LineRight, 200));
        // Kept inside the plot on both sides.
        Assert.Equal(28, ChartGeometry.Tag((30, 80), 30, 16, 28, ChartGeometry.LineRight, 200).X);
        Assert.Equal(200 - ChartGeometry.LineRight - 44, ChartGeometry.Tag((199, 80), 30, 16, 28, ChartGeometry.LineRight, 200).X);
    }

    [Fact]
    public void A_plot_narrower_than_the_tag_puts_it_at_the_left_edge()
    {
        // 60 wide, 44 for the labels and 12 on the right: 4 pixels for a 44-wide tag.
        var tag = ChartGeometry.Tag((50, 80), 30, 16, 44, 12, 60);

        Assert.Equal(44, tag.X);
    }
}
