#if HEARTHPULSE_WPF
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The Tavern Brawl page (docs/brawl.md): this week's class win rates by everyone's games (Hearthpulse data) and the
/// player's own classes in the Brawl played last (by its scenario). Built with My Stats when the history changes.
/// </summary>
public sealed partial class MainViewModel
{
    private BrawlMeta? _brawlMeta;
    private BrawlMeta? _brawlShownMeta;
    private bool _brawlMetaFailed;
    private int _brawlBuild;
    private IReadOnlyList<StatGroupRow> _brawlClasses = [];
    private string _brawlUnclassed = string.Empty;
    private string _brawlSource = string.Empty;
    private IReadOnlyList<StatGroupRow> _brawlMine = [];
    private string _brawlMineNote = string.Empty;
    private bool _brawlMineOld;
    private string _brawlClassesNote = Loc.T("Brawl.Classes.Note");
    private TableSort? _brawlClassesSort;
    private TableSort? _brawlMineSort;

    /// <summary>The community table sorts by its column heads: strongest class first until one is clicked.</summary>
    public TableSort BrawlClassesSort => _brawlClassesSort ??= new TableSort("WinRate", () => BrawlClasses = BrawlClassesSort.Apply(BrawlClasses, (r, c) => r.SortValue(c)), "Name");

    /// <summary>The player's table: most played class first, like My Stats.</summary>
    public TableSort BrawlMineSort => _brawlMineSort ??= GroupSort(() => BrawlMine = BrawlMineSort.Apply(BrawlMine, (r, c) => r.SortValue(c)));

    /// <summary>This week's Brawl by class, everyone's games; no record column (the community has no W/L).</summary>
    public IReadOnlyList<StatGroupRow> BrawlClasses
    {
        get => _brawlClasses;
        private set
        {
            if (Set(ref _brawlClasses, value))
            {
                RaiseBrawlClassesStates();
            }
        }
    }

    /// <summary>No numbers yet and neither numbers nor a failure came from the host.</summary>
    public bool BrawlClassesLoading => _brawlShownMeta is null && !_brawlMetaFailed;

    /// <summary>Nothing to show: the load failed and there is no cache.</summary>
    public bool BrawlClassesUnavailable => _brawlShownMeta is null && _brawlMetaFailed;

    /// <summary>The numbers came, but no archetype of them could be put into a class.</summary>
    public bool BrawlClassesNone => _brawlShownMeta is not null && _brawlClasses.Count == 0;

    /// <summary>"Not in the table: 1,240 games with decks whose class can't be told."; empty when every game is in it.</summary>
    public string BrawlUnclassed { get => _brawlUnclassed; private set => Set(ref _brawlUnclassed, value); }

    /// <summary>"Hearthpulse data · 28,515 games"; empty without any game.</summary>
    public string BrawlSource { get => _brawlSource; private set => Set(ref _brawlSource, value); }

    /// <summary>The player's classes in the Brawl played last.</summary>
    public IReadOnlyList<StatGroupRow> BrawlMine
    {
        get => _brawlMine;
        private set
        {
            if (Set(ref _brawlMine, value))
            {
                Raise(nameof(BrawlMineEmpty));
            }
        }
    }

    /// <summary>No Brawl game at all (with only games from before the scenario was kept, <see cref="BrawlMineOld"/> says why instead).</summary>
    public bool BrawlMineEmpty => _brawlMine.Count == 0 && !_brawlMineOld;

    /// <summary>"The Tavern Brawl you played last: games since October 28."; empty without games.</summary>
    public string BrawlMineNote { get => _brawlMineNote; private set => Set(ref _brawlMineNote, value); }

    /// <summary>Brawl games are kept, but all from before the scenario was recorded: the empty state says so.</summary>
    public bool BrawlMineOld
    {
        get => _brawlMineOld;
        private set
        {
            if (Set(ref _brawlMineOld, value))
            {
                Raise(nameof(BrawlMineEmpty));
            }
        }
    }

    /// <summary>
    /// The community card's note: "this week's Brawl", or, when the numbers are over a week old (the server has not
    /// answered since), when they were fetched instead.
    /// </summary>
    public string BrawlClassesNote { get => _brawlClassesNote; private set => Set(ref _brawlClassesNote, value); }

    /// <summary>
    /// The Brawl numbers the host loaded. <see cref="BrawlMeta.Empty"/> (neither the server nor the cache had any) counts
    /// as a failure, so what an earlier load showed stays.
    /// </summary>
    public void SetBrawlMeta(BrawlMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (ReferenceEquals(meta, BrawlMeta.Empty))
        {
            BrawlMetaUnavailable();
            return;
        }

        _brawlMeta = meta;
        _brawlMetaFailed = false;
        RebuildBrawlStats();
    }

    /// <summary>The host's Brawl load failed; what an earlier load showed stays.</summary>
    public void BrawlMetaUnavailable()
    {
        _brawlMetaFailed = true;
        RaiseBrawlClassesStates();
    }

    private void RaiseBrawlClassesStates()
    {
        Raise(nameof(BrawlClassesLoading));
        Raise(nameof(BrawlClassesUnavailable));
        Raise(nameof(BrawlClassesNone));
    }

    /// <summary>Builds both tables off the UI thread; a result a newer build overtook is dropped.</summary>
    private void RebuildBrawlStats() => BuildBrawlStatsAsync(_allGames, _brawlMeta).Forget("Brawl build");

    private async Task BuildBrawlStatsAsync(IReadOnlyList<GameRecord> all, BrawlMeta? meta)
    {
        var generation = ++_brawlBuild;
        try
        {
            var built = await Task.Run(() => BuildBrawl(all, meta)).ConfigureAwait(true);
            if (generation != _brawlBuild)
            {
                return;
            }

            BrawlClasses = BrawlClassesSort.Apply(StatGroupRow.Marked(built.Classes), (r, c) => r.SortValue(c));
            BrawlUnclassed = built.Unclassed;
            BrawlSource = built.Source;
            BrawlClassesNote = built.Note;
            BrawlMine = BrawlMineSort.Apply(StatGroupRow.Marked(built.Mine), (r, c) => r.SortValue(c));
            BrawlMineNote = built.MineNote;
            BrawlMineOld = built.MineOld;
            // Last: once the states say "shown", every line of this build is in place (a reader off the UI thread
            // waiting on them sees no half-applied build).
            _brawlShownMeta = meta;
            RaiseBrawlClassesStates();
        }
        catch (Exception ex)
        {
            Diag.Error("Brawl build failed", ex);
        }
    }

    private sealed record BrawlBuilt(List<StatGroupRow> Classes, string Unclassed, string Source, string Note, List<StatGroupRow> Mine, string MineNote, bool MineOld);

    /// <summary>Numbers older than this are not "this week's" any more.</summary>
    internal static readonly TimeSpan BrawlStaleAfter = TimeSpan.FromDays(7);

    private static BrawlBuilt BuildBrawl(IReadOnlyList<GameRecord> all, BrawlMeta? meta)
    {
        var culture = CultureInfo.CurrentCulture;
        var classes = (meta?.Classes ?? []).Where(c => c.Games > 0).Select(c => new StatGroupRow(
                OverlayViewModel.ClassName(c.Class),
                ClassHero(c.Class),
                Loc.F("Brawl.Classes.Sub", Loc.P("Stats.Games", c.Games), c.Popularity.ToString("P1", culture)),
                string.Empty,
                c.WinRate.ToString("P1", culture),
                c.WinRate,
                VsGlobal.None,
                c.Games))
            .ToList();
        var unclassed = meta is { OtherGames: > 0 } ? Loc.F("Brawl.Classes.Unclassed", Loc.P("Stats.Games", meta.OtherGames)) : string.Empty;
        var source = meta is { TotalGames: > 0 } ? HearthpulseData.Attribution + " · " + Loc.P("Stats.Games", meta.TotalGames) : string.Empty;
        var note = meta?.FetchedAt is { } fetched && DateTime.UtcNow - fetched > BrawlStaleAfter
            ? Loc.F("Brawl.Classes.NoteStale", fetched.ToLocalTime())
            : Loc.T("Brawl.Classes.Note");

        var latest = BrawlStats.LatestBrawl(all);
        var mine = ConstructedStats.ByPlayerClass(latest).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        var mineNote = latest.Count > 0 ? Loc.F("Brawl.Mine.Note", latest.Min(g => g.StartedAt)) : string.Empty;
        var old = latest.Count == 0 && all.Any(g => g.Mode == "Brawl" && g.GameType == "GT_TAVERNBRAWL" && !g.IsAbandoned);
        return new BrawlBuilt(classes, unclassed, source, note, mine, mineNote, old);
    }
}
