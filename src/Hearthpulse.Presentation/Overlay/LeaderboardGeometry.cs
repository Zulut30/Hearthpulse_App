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

    /// <summary>
    /// Whether overlay <paramref name="x"/> is in the left part of the surface where Hearthstone draws the leaderboard,
    /// so the game is worth asking which hero is under the mouse. Generous: the hero is read from the game precisely
    /// because the tile boxes may not be lined up.
    /// </summary>
    public static bool Near(double x, double surfaceWidth, double surfaceHeight, AppSettings settings) =>
        x >= 0 && x <= Left(surfaceWidth, surfaceHeight) + Math.Max(settings.LeaderboardWidth * surfaceHeight * 2, surfaceHeight * 0.2);

    /// <summary>Leaderboard place (1..8) under the overlay point (<paramref name="x"/>, <paramref name="y"/>), or 0.</summary>
    public static int SlotAt(double x, double y, double surfaceWidth, double surfaceHeight, AppSettings settings, bool duos)
    {
        var left = Left(surfaceWidth, surfaceHeight);
        if (x < left - 8 || x > left + (settings.LeaderboardWidth * surfaceHeight))
        {
            return 0;
        }

        for (var i = 0; i < 8; i++)
        {
            var (top, height) = Tile(i, surfaceHeight, settings, duos);
            if (y >= top && y < top + height)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Top of the opponent-board popup for the pointer at overlay <paramref name="y"/>: a little above it, on the surface.</summary>
    public static double PopupTop(double y, double surfaceHeight) => Math.Clamp(y - 40, 8, Math.Max(8, surfaceHeight - 240));

    /// <summary>Top of the popup shown for place <paramref name="slot"/> (1..8) without a pointer (off-screen renders).</summary>
    public static double SlotPopupTop(int slot, double surfaceHeight, AppSettings settings, bool duos) =>
        Math.Max(8, Tile(slot - 1, surfaceHeight, settings, duos).Top - 20);

    /// <summary>
    /// The calibration guides (Settings: "Show leaderboard guides"): one frame per tile, a pixel inside it top and
    /// bottom so neighbouring frames do not touch.
    /// </summary>
    public static IEnumerable<(double Left, double Top, double Width, double Height)> Guides(double surfaceWidth, double surfaceHeight, AppSettings settings, bool duos)
    {
        var left = Left(surfaceWidth, surfaceHeight);
        for (var i = 0; i < 8; i++)
        {
            var (top, height) = Tile(i, surfaceHeight, settings, duos);
            yield return (left, top + 1, settings.LeaderboardWidth * surfaceHeight, Math.Max(4, height - 2));
        }
    }
}
