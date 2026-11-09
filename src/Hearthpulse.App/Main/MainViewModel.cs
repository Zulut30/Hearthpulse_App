using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    // ---- stats pool -----------------------------------------------------------------------

    private bool _poolAvailable;

    /// <summary>The stats pool is set up; the first start banner and the Settings switch only show then.</summary>
    public bool PoolAvailable
    {
        get => _poolAvailable;
        set
        {
            if (Set(ref _poolAvailable, value))
            {
                Raise(nameof(AskPoolConsent));
                Raise(nameof(ShowGameStatsNotice));
            }
        }
    }

    /// <summary>The player has not said yet whether to share their Battlegrounds results.</summary>
    public bool AskPoolConsent => _poolAvailable
        && (Settings.PoolConsent is null || (Settings.PoolConsent == true && Settings.PoolConsentVersion < PoolClient.ConsentVersion));

    public bool PoolSharing
    {
        get => Settings.PoolConsent == true;
        set
        {
            Settings.PoolConsent = value;
            Settings.PoolConsentVersion = PoolClient.ConsentVersion;
            Raise();
            Raise(nameof(AskPoolConsent));
            PoolConsentChanged?.Invoke();
        }
    }

    /// <summary>The banner's buttons: "yes" shares results, anything else declines.</summary>
    public RelayCommand AnswerPool { get; }

    public event Action? PoolConsentChanged;

    private int CountHistory(IReadOnlyList<GameRecord> recent) => _history.GameCount();

    partial void RestoreRatingFromHistory(string mode, IReadOnlyList<RatingPoint> rating)
    {
        // Before Hearthstone reports a rating this session, show the last one from history.
        if (rating.Count > 0)
        {
            var last = rating[^1].Rating.ToString("N0", CultureInfo.CurrentCulture);
            if (mode == "Duos" && DuosRating == "—")
            {
                DuosRating = last;
            }
            else if (mode != "Duos" && SoloRating == "—")
            {
                SoloRating = last;
            }
        }
    }
}
