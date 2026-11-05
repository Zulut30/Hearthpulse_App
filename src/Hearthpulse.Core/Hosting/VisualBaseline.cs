namespace Hearthpulse.Core.Hosting;

/// <summary>
/// The frames both renderers draw for the visual baselines (render group <c>baseline</c>, compared by
/// <c>tools/visual/visual_check.py</c>): main-window pages, and the overlay's layout mode on game screens of three shapes.
/// Rendered offline on the fixture data folder (<c>tests/visual/data</c>), so they show the same on any PC and day.
/// </summary>
public static class VisualBaseline
{
    /// <summary>Main-window pages, each saved as <c>main-&lt;page&gt;.png</c>.</summary>
    public static IReadOnlyList<string> Pages { get; } = ["Overview", "Battlegrounds", "Standard", "History", "Settings"];

    /// <summary>
    /// Game screens the overlay is drawn on, each saved as <c>overlay-layout-&lt;name&gt;.png</c>: 16:9, 16:10 and 4:3 at
    /// 1200 lines or 1080 (the overlay scales with the screen's height).
    /// </summary>
    public static IReadOnlyList<(string Name, int Width, int Height)> Screens { get; } =
        [("16x9", 1920, 1080), ("16x10", 1920, 1200), ("4x3", 1600, 1200)];
}
