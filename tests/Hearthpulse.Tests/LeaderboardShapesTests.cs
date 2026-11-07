using Hearthpulse.Core;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// T3, overlay positions: the Battlegrounds leaderboard on screens of every shape. The game draws it at the left edge of
/// its centred 4:3 area, so on wide screens it moves right and on 4:3 or narrower it sits at the window's edge; the
/// tiles, the hit test and the popups follow it on every shape, solo and Duos.
/// </summary>
public sealed class LeaderboardShapesTests
{
    public static TheoryData<double, double, double> Shapes => new()
    {
        { 1920, 1080, 240 }, // 16:9
        { 1920, 1200, 160 }, // 16:10
        { 1600, 1200, 0 }, // 4:3
        { 1280, 1024, 0 }, // 5:4: narrower than the game's 4:3 area
        { 2560, 1080, 560 }, // 21:9
        { 5120, 1440, 1600 }, // 32:9
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_leaderboard_sits_at_the_left_of_the_centred_4_3_area(double width, double height, double left)
    {
        var settings = new AppSettings();
        Assert.Equal(left, LeaderboardGeometry.Left(width, height), 6);
        Assert.True(LeaderboardGeometry.Beside(width, height, settings) > left);
        Assert.True(LeaderboardGeometry.Near(left, width, height, settings));
        Assert.False(LeaderboardGeometry.Near(width, width, height, settings));
        Assert.False(LeaderboardGeometry.Near(-1, width, height, settings));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_tile_is_found_under_its_own_middle_and_nowhere_off_the_column(double width, double height, double left)
    {
        var settings = new AppSettings();
        var column = settings.LeaderboardWidth * height;
        foreach (var duos in new[] { false, true })
        {
            var previousBottom = 0.0;
            for (var i = 0; i < 8; i++)
            {
                var (top, tile) = LeaderboardGeometry.Tile(i, height, settings, duos);
                Assert.True(top >= previousBottom - 1e-9, $"{width}x{height} duos={duos}: tile {i} overlaps the one above");
                Assert.True(top + tile <= height, $"{width}x{height} duos={duos}: tile {i} runs off the screen");
                previousBottom = top + tile;

                var middle = top + (tile / 2);
                Assert.Equal(i + 1, LeaderboardGeometry.SlotAt(left + (column / 2), middle, width, height, settings, duos));
                Assert.Equal(0, LeaderboardGeometry.SlotAt(left + column + 1, middle, width, height, settings, duos));
                Assert.Equal(0, LeaderboardGeometry.SlotAt(left - 9, middle, width, height, settings, duos));
            }

            Assert.Equal(0, LeaderboardGeometry.SlotAt(left + (column / 2), height - 1, width, height, settings, duos));
            Assert.All(LeaderboardGeometry.Guides(width, height, settings, duos), g =>
            {
                Assert.Equal(left, g.Left, 6);
                Assert.InRange(g.Top + g.Height, 0, height);
            });
        }
    }
}
