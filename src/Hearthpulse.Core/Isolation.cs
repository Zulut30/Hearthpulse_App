namespace Hearthpulse.Core;

/// <summary>
/// <c>HEARTHPULSE_ISOLATED=1</c>: the run sees nothing outside its data folder. No request leaves the machine (each
/// fails as a lost network would), and no Hearthstone install is found unless the settings name one. The visual
/// baselines (tools/visual) render this way, so a frame never depends on the servers, the PC's game or its logs.
/// </summary>
public static class Isolation
{
    public static bool On => Environment.GetEnvironmentVariable("HEARTHPULSE_ISOLATED") == "1";
}
