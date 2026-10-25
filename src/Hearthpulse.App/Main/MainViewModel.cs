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
    /// <summary>Under the History title: how many games are kept, and that they stay on this PC.</summary>
    public string HistorySubtitle => Loc.P("Main.History.SubtitlePc", HistoryCount);

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

    private bool _tribeRows;

    /// <summary>The stats pool groups final boards by tribe; downloaded stats have comps.</summary>
    public string CompTabLabel => _tribeRows ? Loc.T("Main.Bg.Tribes") : Loc.T("Main.Bg.Comps");

    public string CompCaption => _tribeRows ? Loc.T("Main.Bg.CompCaptionTribes") : Loc.T("Main.Bg.CompCaptionComps");

    public bool HasHeroTiers => _heroTiers.Count > 0;
    public bool HasCompTiers => _compTiers.Count > 0;
    public bool HasTrinketTiers => _trinketTiers.Count > 0;
    public bool HasCardTiers => _cardTiers.Count > 0;

    private int CountHistory(IReadOnlyList<GameRecord> recent) => _history.GameCount();

    private static string TileLabel(string text) => text;

    partial void OnRecentChanged() => Raise(nameof(BannerHero));

    partial void OnHeroTiersChanged() => Raise(nameof(HasHeroTiers));

    partial void OnCompTiersChanged() => Raise(nameof(HasCompTiers));

    partial void OnTrinketTiersChanged() => Raise(nameof(HasTrinketTiers));

    partial void OnCardTiersChanged() => Raise(nameof(HasCardTiers));

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

    // The stats pool's final-board tribes are shown only in the WPF shell.
    private static bool UseTribeRows(BattlegroundsMeta meta) => meta.Comps.Count == 0 && meta.Tribes.Count > 0;

    partial void RebuildTribeRows(BattlegroundsMeta meta)
    {
        // Final boards grouped by main tribe, from the stats pool.
        var tribes = meta.Tribes.OrderBy(t => t.AveragePlacement).ToList();
        var cBest = tribes.Count > 0 ? tribes[0].AveragePlacement : 3;
        var cWorst = tribes.Count > 0 ? tribes[^1].AveragePlacement : 5;
        CompTiers = tribes.Select((t, i) => new MetaCompRow(
                i + 1,
                t.Tribe == "MIXED" ? Loc.T("Main.Bg.Mixed") : Races.Display(t.Tribe, _cards.Locale),
                Loc.P("Main.Bg.TribeGames", t.Games),
                BattlegroundsAdvisor.TierOf(t.AveragePlacement),
                string.Empty,
                t.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                cWorst - cBest < 0.01 ? 1 : 1 - ((t.AveragePlacement - cBest) / (cWorst - cBest) * 0.85),
                Loc.F("Main.Bg.TribeRates", t.Top4Rate, t.FirstRate),
                string.Empty))
            .ToList();
    }

    partial void OnCompRowsRebuilt(bool tribeRows)
    {
        if (_tribeRows != tribeRows)
        {
            _tribeRows = tribeRows;
            Raise(nameof(CompTabLabel));
            Raise(nameof(CompCaption));
        }
    }
}
