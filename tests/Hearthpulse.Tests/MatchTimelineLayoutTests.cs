using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The match player's timeline in both UIs (shared presentation, <c>Main/Match/MatchTimelineLayout.cs</c>), 400 px wide
/// with 20 steps: where stretches, ticks, the position line and the diamond go, their brushes, when labels fit, and the
/// step a click or a drag goes to.
/// </summary>
public sealed class MatchTimelineLayoutTests
{
    private static MatchTimelineSegment Turn(double start, double end, int first, MatchTimelineLook look = MatchTimelineLook.Mine, string label = "3") =>
        new(start, end, first, first + 1, look, label, string.Empty);

    private static MatchTimelineTick Tick(double at, int frame) => new(at, frame, MatchTimelineLook.Mine, string.Empty);

    [Theory]
    [InlineData(MatchTimelineLook.Mine, "AccentSoft", null, "Text")]
    [InlineData(MatchTimelineLook.Theirs, "Accent2Soft", null, "Muted")]
    [InlineData(MatchTimelineLook.Shop, "Surface3", "Border", "Muted")]
    [InlineData(MatchTimelineLook.Won, "PositiveSoft", "Positive", "Muted")]
    [InlineData(MatchTimelineLook.Lost, "NegativeSoft", "Negative", "Muted")]
    [InlineData(MatchTimelineLook.Combat, "Surface4", "Border", "Muted")]
    [InlineData(MatchTimelineLook.Neutral, "Surface4", null, "Muted")]
    public void Each_stretch_takes_its_look_and_the_current_one_the_selected_look(MatchTimelineLook look, string fill, string? edge, string text)
    {
        Assert.Equal((fill, edge, text), MatchTimelineLayout.SpanLook(look, current: false));
        Assert.Equal(("AccentContainer", "Accent", "OnAccentContainer"), MatchTimelineLayout.SpanLook(look, current: true));
    }

    [Fact]
    public void Ticks_are_ours_theirs_or_both_players()
    {
        Assert.Equal("Accent", MatchTimelineLayout.TickBrush(MatchTimelineLook.Mine));
        Assert.Equal("Accent2", MatchTimelineLayout.TickBrush(MatchTimelineLook.Theirs));
        Assert.Equal("Muted", MatchTimelineLayout.TickBrush(MatchTimelineLook.Neutral));
    }

    [Fact]
    public void Labels_and_combat_glyphs_show_only_where_they_fit()
    {
        var turn = Turn(0, 0.05, 0);
        Assert.Equal(18, MatchTimelineLayout.SpanPixels(turn, 400), 9);
        Assert.True(MatchTimelineLayout.ShowsLabel(turn, 18));
        Assert.False(MatchTimelineLayout.ShowsLabel(turn, 17.9));
        Assert.False(MatchTimelineLayout.ShowsLabel(turn with { Label = string.Empty }, 100));
        Assert.False(MatchTimelineLayout.ShowsGlyph(turn, 100));

        var combat = Turn(0, 0.05, 0, MatchTimelineLook.Won);
        Assert.False(MatchTimelineLayout.ShowsLabel(combat, 100));
        Assert.True(MatchTimelineLayout.ShowsGlyph(combat, 16));
        Assert.False(MatchTimelineLayout.ShowsGlyph(combat, 15.9));
    }

    [Fact]
    public void Stretches_ticks_line_and_diamond_sit_at_their_fractions()
    {
        Assert.Equal((101, 16, 98, 22), MatchTimelineLayout.Span(Turn(0.25, 0.5, 5), 400));
        // A stretch thinner than its margins still gets a pixel.
        Assert.Equal(1, MatchTimelineLayout.Span(Turn(0.25, 0.251, 5), 400).Width);
        Assert.Equal((196, 4, 8, 10), MatchTimelineLayout.Tick(Tick(0.5, 7), 400));
        Assert.Equal((199, 4, 2, 34), MatchTimelineLayout.Line(0.5, 400));
        Assert.Equal((196, 0, 8, 8), MatchTimelineLayout.Diamond(0.5, 400));
    }

    [Fact]
    public void A_click_in_the_tick_row_goes_to_the_nearest_tick_within_its_zone()
    {
        MatchTimelineTick[] ticks = [Tick(0.5, 7), Tick(0.52, 9)];

        Assert.Equal(7, MatchTimelineLayout.FrameUnder(ticks, [], 203, 5, 400, 19));
        Assert.Equal(9, MatchTimelineLayout.FrameUnder(ticks, [], 205, 5, 400, 19));
        // No tick within 4 px: the step under the pointer, 300 / 400 of 20 steps.
        Assert.Equal(15, MatchTimelineLayout.FrameUnder(ticks, [], 300, 5, 400, 19));
    }

    [Fact]
    public void A_click_on_the_stretches_goes_to_the_first_step_of_the_one_under_the_pointer()
    {
        MatchTimelineSegment[] spans = [Turn(0, 0.5, 0), Turn(0.5, 1, 10)];

        Assert.Equal(0, MatchTimelineLayout.FrameUnder([], spans, 120, 20, 400, 19));
        // A stretch starts at its edge.
        Assert.Equal(10, MatchTimelineLayout.FrameUnder([], spans, 200, 20, 400, 19));
        // Ticks are not looked at below the tick row; past the last stretch, the last step.
        Assert.Equal(10, MatchTimelineLayout.FrameUnder([Tick(0.5, 7)], spans, 200, 20, 400, 19));
        Assert.Equal(19, MatchTimelineLayout.FrameUnder([], spans, 450, 20, 400, 19));
    }

    [Fact]
    public void A_drag_goes_to_the_step_under_the_pointer_inside_the_game()
    {
        Assert.Equal(10, MatchTimelineLayout.FrameAt(200, 400, 19));
        Assert.Equal(0, MatchTimelineLayout.FrameAt(-30, 400, 19));
        Assert.Equal(19, MatchTimelineLayout.FrameAt(900, 400, 19));
        // Before the first layout the width is 0: taken as 1 px.
        Assert.Equal(19, MatchTimelineLayout.FrameAt(5, 0, 19));
    }
}
