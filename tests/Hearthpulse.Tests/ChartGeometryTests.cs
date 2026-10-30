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
    public void A_sparkline_breaks_where_a_day_has_no_value_and_a_lone_point_is_its_own_run()
    {
        // 100 × 20 with 3 px around: x = 3 + 94 i / 3, y = 3 + 14 (1 - v).
        var runs = SmallChartGeometry.SparkRuns([0.5, null, 1, 0], 4, 100, 20, 0, 1);

        Assert.Equal(2, runs.Count);
        Assert.Equal([(3.0, 10.0)], runs[0]);
        Assert.Equal(3 + (94.0 * 2 / 3), runs[1][0].X, 9);
        Assert.Equal(3, runs[1][0].Y, 9);
        Assert.Equal((97.0, 17.0), runs[1][1]);
    }

    [Fact]
    public void Sparkline_slots_follow_the_longer_series_and_values_stay_inside()
    {
        // The compare series has 5 days: 2 values here still sit on the first two of 5 slots; 2 clamps to the top.
        var runs = SmallChartGeometry.SparkRuns([2, -1], 5, 100, 20, 0, 1);
        Assert.Equal([(3.0, 3.0), (26.5, 17.0)], Assert.Single(runs));

        Assert.Equal(50, Assert.Single(Assert.Single(SmallChartGeometry.SparkRuns([0.5], 1, 100, 20, 0, 1))).X);
        Assert.Empty(SmallChartGeometry.SparkRuns([null, null], 2, 100, 20, 0, 1));
    }

    [Fact]
    public void Mini_bars_fill_to_their_share_of_the_largest_with_the_compare_tick_and_the_marker()
    {
        // 90 wide in 3 slots of 30 with a 4 px gap; 60 high less 15 for the labels.
        var layout = SmallChartGeometry.MiniBars([0.5, 0, 0.25], [0.4], 90, 60, labels: true, marker: 2.5);

        Assert.Equal(45, layout.Plot);
        Assert.Equal([2.0, 32.0, 62.0], layout.Bars.Select(b => b.X));
        Assert.All(layout.Bars, b => Assert.Equal(26, b.Width));
        Assert.Equal([45.0, 0.0, 22.5], layout.Bars.Select(b => b.Fill));
        Assert.Equal([9.5, null, null], layout.Bars.Select(b => b.Tick));
        // Place 2.5: halfway between the centres of bars 2 and 3.
        Assert.Equal(60.5, layout.MarkerX);
    }

    [Fact]
    public void A_tiny_share_still_shows_and_no_marker_without_a_place()
    {
        var layout = SmallChartGeometry.MiniBars([1, 0.001], [], 40, 30, labels: false, marker: double.NaN);

        Assert.Equal(30, layout.Plot);
        Assert.Equal(2, layout.Bars[1].Fill);
        Assert.Null(layout.MarkerX);
        Assert.Null(SmallChartGeometry.MiniBars([1], [], 40, 30, labels: false, marker: 0.5).MarkerX);
    }

    [Fact]
    public void Result_bars_stack_losses_on_wins_scaled_to_the_busiest_column()
    {
        // 200 × 140: two slots of 100, bars 44 wide; 140 - 18 above - 20 for the labels = 102 high; busiest column 4.
        var layout = SmallChartGeometry.ResultBars([3, 1], [1], labels: true, 200, 140);

        Assert.Equal(120, layout.Baseline);
        Assert.Equal(100, layout.Slot);
        Assert.Equal(44, layout.Bar);
        Assert.Equal(new ResultColumn(28, 3, 1, 76.5, 25.5), layout.Columns[0]);
        Assert.Equal(new ResultColumn(128, 1, 0, 25.5, 0), layout.Columns[1]);

        // Without labels the baseline is 4 from the bottom; narrow slots keep bars at 56 % of the slot.
        var plain = SmallChartGeometry.ResultBars([], [2, 0, 0, 0, 0], labels: false, 100, 140);
        Assert.Equal(136, plain.Baseline);
        Assert.Equal(20 * 0.56, plain.Bar, 9);
        Assert.Equal(5, plain.Columns.Count);
    }

    [Fact]
    public void Ratings_spread_across_the_plot_with_at_least_20_points_of_room()
    {
        // 400 × 120 less the insets (44 left, 12 right, 12 top, 10 bottom): x from 44 to 388, 98 high.
        Hearthpulse.Core.Data.RatingPoint[] points = [new(new DateTime(2026, 10, 1), 6400), new(new DateTime(2026, 10, 2), 7250)];

        var plot = ChartGeometry.Rating(points, 400, 120);

        Assert.Equal([44.0, 388.0], plot.At.Select(p => p.X));
        // 12 % of the 850-point range above and below: 6298 to 7352.
        Assert.Equal(12 + (98 * (1 - (102.0 / 1054))), plot.At[0].Y, 9);
        Assert.Equal(12 + (98 * (1 - (952.0 / 1054))), plot.At[1].Y, 9);
        Assert.Equal((6400, 7250), (plot.Min, plot.Max));
        Assert.Equal(plot.At[0].Y, plot.MinY, 9);
        Assert.Equal(plot.At[1].Y, plot.MaxY, 9);

        // An unchanged rating still gets 20 points above and below: the line sits halfway.
        var flat = ChartGeometry.Rating([new(new DateTime(2026, 10, 1), 5000), new(new DateTime(2026, 10, 2), 5000)], 400, 120);
        Assert.All(flat.At, p => Assert.Equal(61, p.Y, 9));
    }

    [Fact]
    public void The_rating_curve_passes_through_every_point_and_never_overshoots_one()
    {
        (double X, double Y)[] points = [(0, 50), (10, 47), (20, 10), (30, 9), (40, 9), (50, 40), (60, 38)];

        var curve = ChartGeometry.MonotoneCurve(points);

        Assert.Equal(points.Length - 1, curve.Length);
        for (var i = 0; i < curve.Length; i++)
        {
            var (from, to) = (points[i], points[i + 1]);
            Assert.Equal((to.X, to.Y), (curve[i].X, curve[i].Y));
            // Control points a third of the way in, and between the two points' heights: no bump past a dip or a peak.
            Assert.Equal(from.X + ((to.X - from.X) / 3), curve[i].X1, 9);
            Assert.Equal(to.X - ((to.X - from.X) / 3), curve[i].X2, 9);
            var (low, high) = (Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y));
            Assert.InRange(curve[i].Y1, low - 1e-9, high + 1e-9);
            Assert.InRange(curve[i].Y2, low - 1e-9, high + 1e-9);
        }

        // A flat stretch stays flat; a peak has a level tangent.
        Assert.Equal((9.0, 9.0), (curve[3].Y1, curve[3].Y2));
        var peak = ChartGeometry.MonotoneCurve([(0, 10), (10, 0), (20, 10)]);
        Assert.Equal(0, peak[0].Y2, 9);
        Assert.Equal(0, peak[1].Y1, 9);
    }

    [Fact]
    public void A_plot_narrower_than_the_tag_puts_it_at_the_left_edge()
    {
        // 60 wide, 44 for the labels and 12 on the right: 4 pixels for a 44-wide tag.
        var tag = ChartGeometry.Tag((50, 80), 30, 16, 44, 12, 60);

        Assert.Equal(44, tag.X);
    }
}
