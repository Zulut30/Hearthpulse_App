using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Where Hearthstone draws the Battlegrounds leaderboard on the overlay surface, in overlay units (the surface is
/// <c>OverlayScale</c>-adjusted 1080 units high), from the calibration in <see cref="AppSettings"/>. One geometry for
/// the hover popup, the calibration guides and the leaderboard badges, so they line up together.
/// </summary>
public static class LeaderboardGeometry
{
    /// <summary>Gap between a tile's right edge and what the overlay draws beside it, in surface heights.</summary>
    public const double Gap = 0.012;

    /// <summary>
    /// Left edge of the leaderboard. Hearthstone draws it at the edge of the centred 4:3 area, so on wide screens it
    /// sits well right of the window edge.
    /// </summary>
    public static double Left(double surfaceWidth, double surfaceHeight) => Math.Max(0, (surfaceWidth - (surfaceHeight * 4.0 / 3.0)) / 2);

    /// <summary>Left edge of what is drawn beside the tiles (the hover popup, the badges).</summary>
    public static double Beside(double surfaceWidth, double surfaceHeight, AppSettings settings) =>
        Left(surfaceWidth, surfaceHeight) + ((settings.LeaderboardWidth + Gap) * surfaceHeight);

    /// <summary>Top and height of leaderboard tile <paramref name="index"/> (0..7).</summary>
    public static (double Top, double Height) Tile(int index, double surfaceHeight, AppSettings settings, bool duos)
    {
        var slot = settings.LeaderboardSlotHeight * surfaceHeight;
        var top = settings.LeaderboardTop * surfaceHeight;
        if (duos)
        {
            // Duos: teams of two tiles with a gap between teams (HDT: 13.7% of the column is spacing).
            const double spacingShare = 0.137;
            var tile = slot * (1 - spacingShare);
            var spacing = slot * 8 * spacingShare / 3;
            return (top + (tile * index) + (spacing * (index / 2)), tile);
        }

        return (top + (slot * index), slot);
    }
}
