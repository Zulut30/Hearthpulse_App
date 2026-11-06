using Hearthpulse.Core;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// Where a movable panel goes and how big it is, and which panel the pointer arms: what both overlays' MovablePanel
/// and tick did each on their own before Etalon Q5d moved it to shared code.
/// </summary>
public sealed class PanelLayoutTests
{
    [Theory]
    [InlineData(1.0, true, 1.05)]
    [InlineData(1.0, false, 0.95)]
    [InlineData(1.58, true, 1.6)]
    [InlineData(1.6, true, 1.6)]
    [InlineData(0.62, false, 0.6)]
    [InlineData(0.6, false, 0.6)]
    [InlineData(1.03, true, 1.08)]
    public void A_wheel_notch_changes_the_size_by_five_percent_within_the_limits(double scale, bool up, double next) =>
        Assert.Equal(next, PanelLayout.StepScale(scale, up));

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(25.9, true)]
    [InlineData(26, false)]
    [InlineData(30, false)]
    [InlineData(500, false)]
    public void The_name_tag_goes_under_a_panel_with_no_room_above_it(double top, bool below)
    {
        var (x, y, under) = PanelLayout.TagSpot(400, top, 300, 200, 250, 26, 4, 1920, 1080);
        Assert.Equal(below, under);
        Assert.Equal(below ? 200 : -26, y);
        Assert.Equal(-4, x);
        // Never past the top of the surface.
        Assert.True(top + y >= 0);
    }

    [Theory]
    [InlineData(1440)]
    [InlineData(1920)]
    [InlineData(2520)]
    public void The_name_tag_of_a_panel_at_the_right_edge_lines_up_with_its_right_side(double surfaceWidth)
    {
        // The session panel's default spot: top right, 12 from the edge, 10 from the top.
        const double width = 220;
        var left = surfaceWidth - 12 - width;
        var (x, y, below) = PanelLayout.TagSpot(left, 10, width, 120, 330, 26, 4, surfaceWidth, 1080);
        Assert.True(below);
        Assert.Equal(width + 4 - 330, x);
        Assert.InRange(left + x, 0, surfaceWidth - 330);
        Assert.InRange(10 + y, 0, 1080 - 26);
        Assert.Equal(120, y);

        // In the middle of the screen it stays above the panel at its left edge, as before.
        Assert.Equal((-4, -26, false), PanelLayout.TagSpot(surfaceWidth / 2, 500, width, 120, 330, 26, 4, surfaceWidth, 1080));
    }

    [Fact]
    public void No_part_of_the_name_tag_leaves_the_surface()
    {
        // A tag wider than a narrow panel at the left edge: it would start left of the screen.
        Assert.Equal(0, PanelLayout.TagSpot(0, 500, 60, 40, 330, 26, 4, 1920, 1080).X);
        // A tag wider than the panel, the panel pinned to the right edge: lined up right, it still fits.
        var (x, _, _) = PanelLayout.TagSpot(1900, 500, 20, 40, 330, 26, 4, 1920, 1080);
        Assert.Equal(1920 - 330, 1900 + x);
        // A panel as tall as the screen at its top: under it would be off the bottom, so the tag stays on the surface.
        var (_, y, below) = PanelLayout.TagSpot(400, 0, 300, 1080, 250, 26, 4, 1920, 1080);
        Assert.True(below);
        Assert.Equal(1080 - 26, y);
        // A tag wider than the whole surface starts at its left edge.
        Assert.Equal(0, 10 + PanelLayout.TagSpot(10, 500, 60, 40, 3000, 26, 4, 1920, 1080).X);
    }

    [Fact]
    public void A_saved_placement_gives_a_spot_and_a_size_on_any_surface()
    {
        var placement = new PanelPlacement { X = 0.25, Y = 0.5, Scale = 1.2 };
        Assert.Equal((480, 540), PanelLayout.PlacedSpot(placement, 1920, 1080));
        Assert.Equal((640, 720), PanelLayout.PlacedSpot(placement, 2560, 1440));
        Assert.Equal(1.2, PanelLayout.PlacedScale(placement));
        Assert.Equal(1.6, PanelLayout.PlacedScale(new PanelPlacement { Scale = 9 }));
        Assert.Equal(0.6, PanelLayout.PlacedScale(new PanelPlacement { Scale = 0 }));

        // Saved back from where the panel is: fractions of the surface.
        var saved = PanelLayout.Placement(480, 540, 1920, 1080, 1.2);
        Assert.Equal((0.25, 0.5, 1.2), (saved.X, saved.Y, saved.Scale));
        // A surface not laid out yet saves the corner.
        var early = PanelLayout.Placement(480, 540, 0, 0, 1);
        Assert.Equal((0, 0), (early.X, early.Y));
    }

    [Fact]
    public void Only_a_panel_anchored_to_the_game_undoes_the_overlay_scale()
    {
        Assert.Equal(1.25, PanelLayout.DefaultMarginDivisor(gameAnchored: true, overlayScale: 1.25));
        Assert.Equal(1, PanelLayout.DefaultMarginDivisor(gameAnchored: false, overlayScale: 1.25));
        Assert.Equal(1, PanelLayout.DefaultMarginDivisor(gameAnchored: true, overlayScale: 0));
    }

    [Fact]
    public void A_panel_past_an_edge_comes_back_inside_and_one_larger_than_the_surface_sits_at_its_corner()
    {
        Assert.Equal((100, 200), PanelLayout.KeepInside(100, 200, 300, 400, 1920, 1080));
        Assert.Equal((1620, 680), PanelLayout.KeepInside(1800, 900, 300, 400, 1920, 1080));
        Assert.Equal((0, 0), PanelLayout.KeepInside(-50, -20, 300, 400, 1920, 1080));
        Assert.Equal((0, 0), PanelLayout.KeepInside(500, 500, 2500, 1200, 1920, 1080));
    }

    [Fact]
    public void A_drag_follows_the_pointer_snaps_to_edges_and_neighbours_and_stays_on_the_surface()
    {
        // Free space: the pointer's move, as is.
        Assert.Equal((600, 300), PanelLayout.DragSpot(500, 250, 100, 50, 200, 100, 1920, 1080, [], []));
        // Near the left edge: the gutter the default spots keep.
        Assert.Equal((PanelSnap.Gutter, 300), PanelLayout.DragSpot(500, 250, -480, 50, 200, 100, 1920, 1080, [], []));
        // Beside a neighbour that ends at 900: the spacing between default spots.
        var (left, _) = PanelLayout.DragSpot(500, 250, 405, 50, 200, 100, 1920, 1080, [(700, 900)], []);
        Assert.Equal(900 + PanelSnap.Spacing, left);
        // Dragged off the surface: kept on it.
        Assert.Equal((1720, 980), PanelLayout.DragSpot(500, 250, 5000, 5000, 200, 100, 1920, 1080, [], []));
        Assert.Equal((0, 0), PanelLayout.DragSpot(500, 250, -5000, -5000, 200, 100, 1920, 1080, [], []));
    }

    [Fact]
    public void A_look_is_never_fainter_than_the_least_opacity_and_no_look_is_opaque()
    {
        Assert.Equal(1, PanelLayout.LookOpacity(null));
        Assert.Equal(0.7, PanelLayout.LookOpacity(new PanelLook { Opacity = 0.7 }));
        Assert.Equal(PanelLook.MinOpacity, PanelLayout.LookOpacity(new PanelLook { Opacity = 0.05 }));
        Assert.Equal(1, PanelLayout.LookOpacity(new PanelLook { Opacity = 3 }));
    }

    [Fact]
    public void Shown_panels_and_full_size_placeholders_pull_a_drag_and_a_rows_label_does_not()
    {
        Assert.True(PanelLayout.PullsDrags(visible: true, width: 200, shown: true, editing: false, stretchedPlaceholder: false));
        Assert.True(PanelLayout.PullsDrags(visible: true, width: 200, shown: false, editing: true, stretchedPlaceholder: true));
        Assert.False(PanelLayout.PullsDrags(visible: true, width: 200, shown: false, editing: true, stretchedPlaceholder: false));
        Assert.False(PanelLayout.PullsDrags(visible: true, width: 200, shown: false, editing: false, stretchedPlaceholder: true));
        Assert.False(PanelLayout.PullsDrags(visible: false, width: 200, shown: true, editing: false, stretchedPlaceholder: true));
        Assert.False(PanelLayout.PullsDrags(visible: true, width: 0, shown: true, editing: false, stretchedPlaceholder: true));
    }

    [Fact]
    public void The_panel_under_the_pointer_arms_or_offers_its_lock_and_a_dragged_one_stays_armed()
    {
        var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var deck = new Panel("deck");
        var opponent = new Panel("opponent");
        var mulligan = new Panel("mulligan") { SitsOnGame = true };
        IArmablePanel[] panels = [deck, opponent, mulligan];
        var arming = new PanelArming();

        // Ctrl over the deck: only the deck takes the mouse and offers its lock.
        Assert.Equal((true, false), arming.Apply(panels, deck, editing: false, controlDown: true, unlocked: false, t0));
        Assert.Equal((true, true), (deck.IsDragArmed, deck.OffersLock));
        Assert.Equal((false, false), (opponent.IsDragArmed, opponent.OffersLock));
        Assert.Contains("Overlay panel deck armed (Ctrl)", TestDataFolder.ReadLog(), StringComparison.Ordinal);

        // Unlocked panels arm without Ctrl, except one on the game's cards.
        Assert.Equal((true, false), arming.Apply(panels, opponent, editing: false, controlDown: false, unlocked: true, t0.AddSeconds(1)));
        Assert.Equal((false, false), (deck.IsDragArmed, deck.OffersLock));
        Assert.True(opponent.IsDragArmed);
        Assert.Equal((false, false), arming.Apply(panels, mulligan, editing: false, controlDown: false, unlocked: true, t0.AddSeconds(2)));
        Assert.Equal((false, false), (mulligan.IsDragArmed, mulligan.OffersLock));

        // A panel being dragged keeps the mouse wherever the pointer goes, until the button comes up.
        opponent.IsDragging = true;
        Assert.Equal((false, true), arming.Apply(panels, null, editing: false, controlDown: false, unlocked: false, t0.AddSeconds(3)));
        Assert.True(opponent.IsDragArmed);
        // In layout mode every panel moves anyway: none is armed for it.
        Assert.Equal((false, true), arming.Apply(panels, null, editing: true, controlDown: false, unlocked: false, t0.AddSeconds(4)));
        Assert.False(opponent.IsDragArmed);
    }

    private sealed class Panel(string id) : IArmablePanel
    {
        public string Id { get; } = id;

        public bool SitsOnGame { get; init; }

        public bool IsDragging { get; set; }

        public bool IsDragArmed { get; set; }

        public bool OffersLock { get; set; }
    }
}
