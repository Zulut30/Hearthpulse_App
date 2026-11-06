using Hearthpulse.Core.Pool;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    /// <summary>
    /// Settings → Data: send every finished game (all modes) as a game record for Hearthpulse's statistics
    /// (docs/game-stats.md). On by default; turning it off (<see cref="Core.AppSettings.GameStatsOff"/>) asks the pool
    /// to delete what this PC sent.
    /// </summary>
    public bool GameStatsSharing
    {
        get => !Settings.GameStatsOff;
        set
        {
            if (GameStatsSharing == value)
            {
                return;
            }

            Settings.GameStatsOff = !value;
            Raise();
            GameStatsChanged?.Invoke(value);
        }
    }

    /// <summary>The switch shows only once the stats pool is set up.</summary>
    public bool GameStatsAvailable => PoolClient.Available(Settings);

    /// <summary>The game records switch changed (true = on): the host saves the settings and tells the sender.</summary>
    public event Action<bool>? GameStatsChanged;
}
