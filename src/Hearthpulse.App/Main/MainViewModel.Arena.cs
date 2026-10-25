using System.Globalization;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.App.Main;

/// <summary>A card of the Arena page's deck score: its name and ArenaSmith score.</summary>
public sealed record ArenaCardLine(string CardId, string Name, string Score, string Tier);

/// <summary>One bar of a deck's mana curve on the Arena page.</summary>
public sealed record ArenaCurveColumn(string Label, int Count, double Height);

/// <summary>
/// Arena page additions (docs/arena-draft.md): the score of the latest run's deck (ArenaSmith average, curve, strongest
/// and weakest cards), each run's deck score, and the community class win rates. Built with My Stats Arena.
/// </summary>
public sealed partial class MainViewModel
{
    private const double ArenaCurveHeight = 46;

    private ArenaMeta? _arenaMeta;
    private bool _hasArenaDeck;
    private string _arenaDeckTitle = string.Empty;
    private string _arenaDeckAverage = "—";
    private string _arenaDeckTier = string.Empty;
    private string _arenaDeckNote = string.Empty;
    private IReadOnlyList<ArenaCurveColumn> _arenaDeckCurve = [];
    private IReadOnlyList<ArenaCardLine> _arenaDeckBest = [];
    private IReadOnlyList<ArenaCardLine> _arenaDeckWeakest = [];
    private IReadOnlyList<StatGroupRow> _arenaCommunity = [];
    private string _arenaCommunitySource = string.Empty;

    /// <summary>The latest run has a deck with scored cards.</summary>
    public bool HasArenaDeck { get => _hasArenaDeck; private set => Set(ref _hasArenaDeck, value); }

    /// <summary>"Warlock · 7–3 · 2 days ago".</summary>
    public string ArenaDeckTitle { get => _arenaDeckTitle; private set => Set(ref _arenaDeckTitle, value); }

    public string ArenaDeckAverage { get => _arenaDeckAverage; private set => Set(ref _arenaDeckAverage, value); }

    public string ArenaDeckTier { get => _arenaDeckTier; private set => Set(ref _arenaDeckTier, value); }

    /// <summary>"ArenaSmith average over 28 of 30 cards".</summary>
    public string ArenaDeckNote { get => _arenaDeckNote; private set => Set(ref _arenaDeckNote, value); }

    public IReadOnlyList<ArenaCurveColumn> ArenaDeckCurve { get => _arenaDeckCurve; private set => Set(ref _arenaDeckCurve, value); }

    public IReadOnlyList<ArenaCardLine> ArenaDeckBest { get => _arenaDeckBest; private set => Set(ref _arenaDeckBest, value); }

    public IReadOnlyList<ArenaCardLine> ArenaDeckWeakest { get => _arenaDeckWeakest; private set => Set(ref _arenaDeckWeakest, value); }

    /// <summary>Community class win rates (Hearthpulse data), best first.</summary>
    public IReadOnlyList<StatGroupRow> ArenaCommunity { get => _arenaCommunity; private set => Set(ref _arenaCommunity, value); }

    /// <summary>"Hearthpulse data · ArenaSmith score".</summary>
    public string ArenaCommunitySource { get => _arenaCommunitySource; private set => Set(ref _arenaCommunitySource, value); }

    /// <summary>The Arena numbers the host loaded; the Arena page is rebuilt with them.</summary>
    public void SetArenaMeta(ArenaMeta? meta)
    {
        _arenaMeta = meta;
        RebuildArenaStats();
    }

    /// <summary>"Deck score 41" for a run's row, with its tier; empty without a scored deck.</summary>
    private static (string Text, string Tier) RunDeckScore(ArenaRun run, CardDb cards, ArenaMeta? meta) =>
        meta is { HasCards: true } && ArenaRunDeck.ScoreOf(run, meta, cards) is { Average: { } average } score
            ? (Loc.F("Arena.Page.RunDeck", average.ToString("0", CultureInfo.CurrentCulture)), score.Tier ?? string.Empty)
            : (string.Empty, string.Empty);

    /// <summary>Runs on the stats thread with the rest of My Stats Arena; the returned action applies the result.</summary>
    private static Action<MainViewModel> BuildArenaDeck(IReadOnlyList<ArenaRun> runs, CardDb cards, ArenaMeta? meta)
    {
        var culture = CultureInfo.CurrentCulture;
        var latest = meta is { HasCards: true }
            ? runs.Reverse().Select(r => (Run: r, Report: ArenaRunDeck.Report(r, meta, cards))).FirstOrDefault(x => x.Report is not null)
            : default;
        var community = (meta?.Classes ?? []).Select(c => new StatGroupRow(
                OverlayViewModel.ClassName(c.PlayerClass),
                ClassHero(c.PlayerClass),
                Loc.P("Arena.Page.Drafts", c.Drafts) + (c.SevenPlus is { } seven ? " · " + Loc.F("Arena.SevenPlus", seven.ToString("P0", culture)) : string.Empty),
                string.Empty,
                c.WinRate.ToString("P1", culture),
                c.WinRate,
                VsGlobal.None))
            .ToList();
        var source = Loc.F("Arena.Source", ArenaMeta.Attribution);
        if (latest.Report is not { } report)
        {
            return vm =>
            {
                vm.HasArenaDeck = false;
                vm.ArenaCommunity = community;
                vm.ArenaCommunitySource = source;
            };
        }

        var run = latest.Run;
        var title = $"{OverlayViewModel.ClassName(run.PlayerClass)} · {run.Wins}–{run.Losses} · {When(run.Started)}";
        var most = Math.Max(1, report.Score.Curve.Max());
        var curve = report.Score.Curve.Select((count, cost) => new ArenaCurveColumn(
                cost == ArenaDraft.CurveBuckets - 1 ? $"{cost}+" : cost.ToString(culture),
                count,
                count == 0 ? 2 : Math.Max(4, ArenaCurveHeight * count / most)))
            .ToList();
        static ArenaCardLine Line(ArenaDeckCard c) =>
            new(c.CardId, c.Count > 1 ? $"{c.Name} ×{c.Count}" : c.Name, c.Score?.ToString("0", CultureInfo.CurrentCulture) ?? "—", c.Tier ?? string.Empty);
        var best = report.Best.Select(Line).ToList();
        var weakest = report.Weakest.Select(Line).ToList();
        var average = report.Score.Average!.Value.ToString("0.0", culture);
        var note = Loc.F("Arena.Page.DeckNote", report.Score.Scored, report.Score.Cards);
        return vm =>
        {
            vm.HasArenaDeck = true;
            vm.ArenaDeckTitle = title;
            vm.ArenaDeckAverage = average;
            vm.ArenaDeckTier = report.Score.Tier ?? string.Empty;
            vm.ArenaDeckNote = note;
            vm.ArenaDeckCurve = curve;
            vm.ArenaDeckBest = best;
            vm.ArenaDeckWeakest = weakest;
            vm.ArenaCommunity = community;
            vm.ArenaCommunitySource = source;
        };
    }
}
