#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core.GameStats;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    private SourceCaptureState _sourceCaptureState = new("disabled", 0, 0, 0);

    public string SourceCaptureStatusText => _sourceCaptureState.PendingDeletions > 0
        ? Loc.T("Settings.Data.SourceDeleting")
        : Settings.GameStatsOff ? Loc.T("Settings.Data.SourceOff")
        : Settings.SourceCaptureNoticeVersion < SourceCaptureClient.NoticeVersion ? Loc.T("Settings.Data.SourceNotice")
        : _sourceCaptureState.Dropped > 0 ? Loc.F("Settings.Data.SourceDropped", _sourceCaptureState.Dropped)
        : _sourceCaptureState.Status is "unavailable" or "disk_error" or "rejected" or "corrupt_local" or "invalid_receipt" ? Loc.T("Settings.Data.SourceUnavailable")
        : _sourceCaptureState.PendingCaptures > 0 ? Loc.F("Settings.Data.SourcePending", _sourceCaptureState.PendingCaptures)
        : Loc.T("Settings.Data.SourceReady");

    public void UpdateSourceCaptureState(SourceCaptureState state)
    {
        _sourceCaptureState = state;
        Raise(nameof(SourceCaptureStatusText));
    }
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
            // Turned on in Settings: the player has seen what it sends there.
            Settings.GameStatsNoticeShown = true;
            if (value)
            {
                Settings.SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion;
                GameStatsClient.InstallId(Settings);
            }

            Raise();
            Raise(nameof(ShowGameStatsNotice));
            Raise(nameof(SourceCaptureStatusText));
            GameStatsChanged?.Invoke(value);
        }
    }

    /// <summary>The switch shows only once the stats pool is set up.</summary>
    public bool GameStatsAvailable => PoolClient.Available(Settings);

    /// <summary>
    /// The one-time notice in the main window: game records are on by default, what they hold and how to turn them off
    /// (not a consent question). Nothing is sent before it was shown; in WPF it waits for the stats pool's question.
    /// </summary>
    public bool ShowGameStatsNotice => GameStatsAvailable && (!Settings.GameStatsNoticeShown ||
        !Settings.GameStatsOff && Settings.SourceCaptureNoticeVersion < SourceCaptureClient.NoticeVersion)
#if HEARTHPULSE_WPF
        && !AskPoolConsent
#endif
        ;

    private RelayCommand? _answerGameStats;

    /// <summary>The notice's buttons: "ok" keeps sending, "off" turns it off, "more" opens the privacy policy.</summary>
    public RelayCommand AnswerGameStats => _answerGameStats ??= new RelayCommand(p =>
    {
        if (p as string == "more")
        {
            OpenPrivacy.Execute(null);
            return;
        }

        Settings.GameStatsNoticeShown = true;
        Settings.SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion;
        if (p as string == "off")
        {
            Settings.GameStatsOff = true;
        }
        else
        {
            // The install id comes from the pool secret: made now, saved with this answer.
            GameStatsClient.InstallId(Settings);
        }

        Raise(nameof(ShowGameStatsNotice));
        Raise(nameof(GameStatsSharing));
        Raise(nameof(SourceCaptureStatusText));
        GameStatsChanged?.Invoke(GameStatsSharing);
    });

    /// <summary>The game records switch changed (true = on): the host saves the settings and tells the sender.</summary>
    public event Action<bool>? GameStatsChanged;
}
