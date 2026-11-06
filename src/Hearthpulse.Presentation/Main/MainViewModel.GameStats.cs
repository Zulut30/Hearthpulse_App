using Hearthpulse.Core.GameStats;
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
    /// (<see cref="Core.AppSettings.GameStatsConsent"/>, docs/game-stats.md). Off until the player turns it on here: its
    /// own consent, apart from the stats pool's question (another purpose). Switching it off asks the pool to delete
    /// what this PC sent.
    /// </summary>
    public bool GameStatsSharing
    {
        get => Settings.GameStatsConsent >= GameStatsClient.ConsentVersion;
        set
        {
            if (GameStatsSharing == value)
            {
                return;
            }

            Settings.GameStatsConsent = value ? GameStatsClient.ConsentVersion : 0;
            if (value)
            {
                // The install id comes from the pool secret: make it now, so the host saves it with this change and a
                // crash before exit cannot leave sent records that a later "off" no longer finds.
                GameStatsClient.InstallId(Settings);
            }

            Raise();
            GameStatsChanged?.Invoke(value);
        }
    }

    /// <summary>The switch shows only once the stats pool is set up.</summary>
    public bool GameStatsAvailable => PoolClient.Available(Settings);

    /// <summary>The game records switch changed (true = on): the host saves the settings and tells the sender.</summary>
    public event Action<bool>? GameStatsChanged;
}
