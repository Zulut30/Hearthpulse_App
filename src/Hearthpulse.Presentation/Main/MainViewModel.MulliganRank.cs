#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The mulligan's rank filter on the meta page (plan 2.4): All / Diamond / Legend / Top Legend, Legend by default. At
/// Legend the list is the archetype's own numbers (HSReplay, else HSGuru's Legend analysis), as before; at another rank
/// it is HSGuru's mulligan slice of that rank (<see cref="MulliganGuide.SourceRank"/>), all ranks when that slice is thin,
/// and the source line under the title says which. The overlay's guide follows the same setting (the host passes it).
/// </summary>
public sealed partial class MainViewModel
{
    private RelayCommand? _setMulliganRank;
    private string _mulliganSource = string.Empty;
    private CancellationTokenSource? _mulliganLoad;

    /// <summary>
    /// Loads an archetype's mulligan at a filter rank other than Legend: archetype, format, filter rank → the slice with its
    /// base win rate (MulliganGuideSource.LoadRankSliceAsync, the same numbers as the overlay), or null. Set by the host;
    /// without it the list stays at Legend.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<RankSlice?>>? LoadRankMulligan { get; set; }

    /// <summary>Raised after the player picks a mulligan rank (the host saves the settings and tells the overlay's guide).</summary>
    public event Action? MulliganRankChanged;

    /// <summary>
    /// Under the matchup heat map: the Diamond tier list's matchups are HSGuru's Diamond 4-1 (the API serves no Diamond
    /// matchups; HearthpulseData.MatchupRank). Empty at the other ranks.
    /// </summary>
    public string MatchupsRankNote => Settings.MetaRank == "diamond" ? Loc.F("Main.Meta.MatchupsRankNote", Loc.T("Core.MulliganRankSource.diamond")) : string.Empty;

    public IReadOnlyList<MetaChoice> MulliganRanks =>
        MulliganGuide.Ranks.Select(r => new MetaChoice(r, Loc.T("Core.MulliganRank." + r), r == CurrentMulliganRank)).ToList();

    /// <summary>Where the mulligan numbers come from when not the archetype's own (Legend): rank, or the all-ranks fallback.</summary>
    public string MulliganSource { get => _mulliganSource; private set => Set(ref _mulliganSource, value); }

    public RelayCommand SetMulliganRank => _setMulliganRank ??= new RelayCommand(p =>
    {
        if (p is not string rank || !MulliganGuide.Ranks.Contains(rank) || rank == CurrentMulliganRank)
        {
            return;
        }

        Settings.MulliganRank = rank;
        Raise(nameof(MulliganRanks));
        MulliganRankChanged?.Invoke();
        RebuildArchetypeDetail();
    });

    private string CurrentMulliganRank => MulliganGuide.Ranks.Contains(Settings.MulliganRank) ? Settings.MulliganRank : MulliganGuide.DefaultRank;

    /// <summary>
    /// At a rank other than Legend: loads the selected archetype's HSGuru slice and shows it (rows, source line). Returns
    /// false at Legend, or without a loader, so the caller shows the archetype's own numbers.
    /// </summary>
    private bool ShowRankMulligan(MetaArchetype archetype)
    {
        _mulliganLoad?.Cancel();
        var rank = CurrentMulliganRank;
        if (MulliganGuide.SourceRank(rank) == MulliganGuide.DefaultRank || LoadRankMulligan is not { } load)
        {
            MulliganSource = string.Empty;
            return false;
        }

        var cancel = _mulliganLoad = new CancellationTokenSource();
        var format = Settings.MetaFormat;
        Mulligan = [];
        MulliganSource = Loc.T("Main.Meta.LoadingShort");
        LoadRankMulliganAsync(load, archetype, format, rank, cancel.Token).Forget("Mulligan at a rank");
        return true;
    }

    private async Task LoadRankMulliganAsync(Func<string, string, string, CancellationToken, Task<RankSlice?>> load, MetaArchetype archetype,
        string format, string rank, CancellationToken cancel)
    {
        RankSlice? found;
        try
        {
            found = await load(archetype.Key, format, rank, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancel.IsCancellationRequested)
        {
            return;
        }

        ShowRankSlice(found, rank);
    }

    /// <summary>Shows a loaded slice (or that there is none, offline included) with its source line.</summary>
    internal void ShowRankSlice(RankSlice? found, string rank)
    {
        var label = Loc.T("Core.MulliganRankSource." + rank);
        if (found is null)
        {
            Mulligan = [];
            MulliganSource = Loc.F("Main.Meta.MulliganRankNone", label);
            return;
        }

        MulliganSource = found.AllRanks ? Loc.F("Main.Meta.MulliganRankFallback", label) : Loc.F("Main.Meta.MulliganRankFrom", label);
        Mulligan = MulliganRowsOf(found.Slice, found.Baseline);
    }

    /// <summary>The slice's cards, best to keep first: win rate when kept = the base win rate + the kept impact.</summary>
    internal IReadOnlyList<MulliganRow> MulliganRowsOf(MulliganSlice slice, double baseline) => slice.Cards
        .Where(c => c is { Kept: >= MulliganGuide.MinSliceCount, KeptImpact: not null })
        .OrderByDescending(c => c.KeptImpact)
        .Take(12)
        .Select(c => new MulliganRow(
            c.CardId,
            _cards.NameOf(c.CardId),
            (baseline + c.KeptImpact!.Value).ToString("P0", CultureInfo.CurrentCulture),
            c.KeptImpact.Value,
            Loc.F("Main.Meta.Kept", (c.KeepRate ?? 0).ToString("P0", CultureInfo.CurrentCulture)),
            Loc.P("Main.Meta.SeenIn", c.Kept)))
        .ToList();
}
