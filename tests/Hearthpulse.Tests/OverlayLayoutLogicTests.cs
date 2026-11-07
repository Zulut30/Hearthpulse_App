using Hearthpulse.Core;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// What the overlay's code-behind did before it moved to shared code (Q5c): where the leaderboard's places, popup and
/// calibration guides are, the panel layout kept in the settings, the toast's time, and the background read of the
/// hovered leaderboard hero. Both overlays (WPF window, Avalonia view) call these now.
/// </summary>
public sealed class OverlayLayoutLogicTests
{
    // A 16:9 surface: the leaderboard starts right of the window edge, at the centred 4:3 area.
    private const double Width = 1920;
    private const double Height = 1080;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_point_on_a_tile_names_its_place_and_a_point_off_the_column_none(bool duos)
    {
        var settings = new AppSettings();
        var left = LeaderboardGeometry.Left(Width, Height);
        Assert.Equal(240, left);
        for (var i = 0; i < 8; i++)
        {
            var (top, height) = LeaderboardGeometry.Tile(i, Height, settings, duos);
            Assert.Equal(i + 1, LeaderboardGeometry.SlotAt(left + 1, top + (height / 2), Width, Height, settings, duos));
        }

        var middle = LeaderboardGeometry.Tile(3, Height, settings, duos).Top + 1;
        // Eight units of slack left of the column, none right of it.
        Assert.Equal(4, LeaderboardGeometry.SlotAt(left - 8, middle, Width, Height, settings, duos));
        Assert.Equal(0, LeaderboardGeometry.SlotAt(left - 8.5, middle, Width, Height, settings, duos));
        Assert.Equal(4, LeaderboardGeometry.SlotAt(left + 108, middle, Width, Height, settings, duos));
        Assert.Equal(0, LeaderboardGeometry.SlotAt(left + 108.5, middle, Width, Height, settings, duos));
        // Above the first tile and below the last one.
        Assert.Equal(0, LeaderboardGeometry.SlotAt(left + 1, LeaderboardGeometry.Tile(0, Height, settings, duos).Top - 0.5, Width, Height, settings, duos));
        var (lastTop, lastHeight) = LeaderboardGeometry.Tile(7, Height, settings, duos);
        Assert.Equal(0, LeaderboardGeometry.SlotAt(left + 1, lastTop + lastHeight, Width, Height, settings, duos));
    }

    [Fact]
    public void The_game_is_asked_only_with_the_pointer_in_the_leaderboards_part_of_the_surface()
    {
        var settings = new AppSettings();
        // Left edge 240 plus the larger of two tile widths (216) and a fifth of the height (216).
        Assert.False(LeaderboardGeometry.Near(-0.5, Width, Height, settings));
        Assert.True(LeaderboardGeometry.Near(0, Width, Height, settings));
        Assert.True(LeaderboardGeometry.Near(456, Width, Height, settings));
        Assert.False(LeaderboardGeometry.Near(456.5, Width, Height, settings));
        settings.LeaderboardWidth = 0.2;
        Assert.True(LeaderboardGeometry.Near(672, Width, Height, settings));
        Assert.False(LeaderboardGeometry.Near(672.5, Width, Height, settings));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_guide_frames_each_tile_a_unit_inside_it(bool duos)
    {
        var settings = new AppSettings();
        var guides = LeaderboardGeometry.Guides(Width, Height, settings, duos).ToList();
        Assert.Equal(8, guides.Count);
        for (var i = 0; i < 8; i++)
        {
            var (top, height) = LeaderboardGeometry.Tile(i, Height, settings, duos);
            Assert.Equal((240, top + 1, 108, Math.Max(4, height - 2)), guides[i]);
        }

        // Tiles calibrated to nearly nothing still get a frame that can be seen.
        settings.LeaderboardSlotHeight = 0.001;
        Assert.All(LeaderboardGeometry.Guides(Width, Height, settings, duos), g => Assert.Equal(4, g.Height));
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.0, 1.0)]
    [InlineData(0.2, 0.1, 0.6, 0.3)]
    [InlineData(3.0, 2.0, 1.6, 1.0)]
    [InlineData(1.25, 0.55, 1.25, 0.55)]
    public void Overlay_size_and_opacity_stay_in_their_range_whatever_the_settings_file_says(double scale, double opacity, double shownScale, double shownOpacity)
    {
        var settings = new AppSettings { OverlayScale = scale, OverlayOpacity = opacity };
        Assert.Equal(shownScale, OverlayViewModel.SurfaceScale(settings));
        Assert.Equal(shownOpacity, OverlayViewModel.SurfaceOpacity(settings));
    }

    [Fact]
    public void A_moved_panel_and_its_look_are_kept_until_put_back_and_a_reset_clears_them_all()
    {
        var settings = new AppSettings();
        var placement = new PanelPlacement { X = 0.4, Y = 0.2, Scale = 1.2 };
        var look = new PanelLook { Opacity = 0.7 };
        OverlayViewModel.SavePlacement(settings, "deck", placement);
        OverlayViewModel.SaveLook(settings, "deck", look);
        OverlayViewModel.SavePlacement(settings, "opponent", new PanelPlacement());
        Assert.Same(placement, settings.Panels["deck"]);
        Assert.Same(look, settings.PanelLooks["deck"]);
        Assert.Contains("Overlay panel deck placed at 0.4, 0.2, size 1.2", TestDataFolder.ReadLog(), StringComparison.Ordinal);

        OverlayViewModel.SavePlacement(settings, "deck", null);
        OverlayViewModel.SaveLook(settings, "deck", null);
        Assert.False(settings.Panels.ContainsKey("deck"));
        Assert.False(settings.PanelLooks.ContainsKey("deck"));
        Assert.True(settings.Panels.ContainsKey("opponent"));

        OverlayViewModel.SaveLook(settings, "secrets", look);
        OverlayViewModel.ClearLayout(settings);
        Assert.Empty(settings.Panels);
        Assert.Empty(settings.PanelLooks);
    }

    [Fact]
    public void Showing_the_toast_or_the_move_tip_starts_their_time_and_hiding_takes_both_down()
    {
        var vm = new OverlayViewModel();
        Assert.Equal(TimeSpan.FromSeconds(9), OverlayViewModel.ToastTime);
        Assert.False(vm.ToastJustShown(nameof(OverlayViewModel.ShowToast)));
        vm.ShowToast = true;
        Assert.True(vm.ToastJustShown(nameof(OverlayViewModel.ShowToast)));
        Assert.False(vm.ToastJustShown(nameof(OverlayViewModel.ShowMoveTip)));
        Assert.False(vm.ToastJustShown(null));
        vm.ShowMoveTip = true;
        Assert.True(vm.ToastJustShown(nameof(OverlayViewModel.ShowMoveTip)));

        vm.HideToasts();
        Assert.False(vm.ShowToast);
        Assert.False(vm.ShowMoveTip);
    }

    [Fact]
    public void The_hovered_hero_is_read_in_the_background_at_most_four_times_a_second()
    {
        var reader = new HoveredHeroReader();
        var start = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var reads = 0;
        string? Read()
        {
            Interlocked.Increment(ref reads);
            return "BG_HERO_01";
        }

        reader.Poll(Read, start);
        Assert.True(SpinWait.SpinUntil(() => reader.Hero == "BG_HERO_01", TimeSpan.FromMinutes(1)));
        // Within a quarter second of the last read the game is not asked again.
        reader.Poll(Read, start.AddMilliseconds(249));
        Assert.Equal(1, Volatile.Read(ref reads));

        reader.Clear();
        Assert.Null(reader.Hero);
        // Done reading (the busy mark goes after the answer is kept): the next poll after the quarter second reads again.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            reader.Poll(Read, start.AddSeconds(1));
            return Volatile.Read(ref reads) == 2;
        }, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_read_that_fails_leaves_no_hero_and_is_logged()
    {
        var reader = new HoveredHeroReader();
        var start = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        reader.Poll(() => "BG_HERO_02", start);
        Assert.True(SpinWait.SpinUntil(() => reader.Hero == "BG_HERO_02", TimeSpan.FromMinutes(1)));

        var failed = new ManualResetEventSlim();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            reader.Poll(() =>
            {
                failed.Set();
                throw new InvalidOperationException("memory read failed in a test");
            }, start.AddSeconds(1));
            return failed.IsSet;
        }, TimeSpan.FromMinutes(1)));
        Assert.True(SpinWait.SpinUntil(() => reader.Hero is null, TimeSpan.FromMinutes(1)));
        Assert.True(SpinWait.SpinUntil(() => TestDataFolder.ReadLog().Contains("memory read failed in a test", StringComparison.Ordinal), TimeSpan.FromMinutes(1)));
    }
}
