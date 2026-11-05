namespace Hearthpulse.Core;

/// <summary>
/// <c>HEARTHPULSE_ISOLATED=1</c>: the run sees nothing outside its data folder. No request leaves the machine (each
/// fails as a lost network would), no Hearthstone install is found unless the settings name one, and the text font is
/// one every system has. The visual baselines (tools/visual) render this way, so a frame never depends on the servers,
/// the PC's game, its logs or its fonts.
/// </summary>
public static class Isolation
{
    public static bool On => Forced.Value ?? Environment.GetEnvironmentVariable("HEARTHPULSE_ISOLATED") == "1";

    /// <summary>A test's own setting, for its async flow only: tests run side by side.</summary>
    internal static AsyncLocal<bool?> Forced { get; } = new();
}
