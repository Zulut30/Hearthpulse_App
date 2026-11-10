#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// What a Battlegrounds tier tab shows around its rows: with rows, the line "source · slice · date" above the list
/// (<see cref="Source"/>); without, one empty state (<see cref="EmptyText"/> and its glyph) in place of the line, the
/// column heads and the list.
/// </summary>
public sealed record BgTabState(bool HasRows, string Source, string EmptyText, string EmptyGlyph) : IImmutableView;

/// <summary>
/// The service's slices on demand, as the hosts wire them: <c>BgSiteClient.LoadHeroesAsync</c> (Duos or not, MMR
/// slice) and <c>LoadTrinketsAsync</c> (MMR slice). Null from either is "no data for the slice".
/// </summary>
public sealed record BgSiteSliceLoader(Func<bool, string, Task<BgSiteTierList?>> Heroes, Func<string, Task<BgSiteTierList?>> Trinkets);

/// <summary>
/// The Battlegrounds tier tabs (Heroes, Comps, Trinkets, Cards) from the site's open service
/// (docs/bg-site-stats.md): each tab is one flat list a view binds once (a <see cref="TierHeadRow"/>, then the tier's
/// rows, in the service's order and with the service's tier letters), a line naming the source, the slice and the
/// date, and one empty state. Heroes and trinkets have MMR slices: the default one comes with the refresh, another is
/// asked for when the player picks it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Whether a comp shows the service's tip text. The texts are the origin's own writing, and showing them is the
    /// owner's decision (LEGAL-HeartLens-bg-site-stats.md, L-M1): off until the owner says otherwise. The one place
    /// that decides it.
    /// </summary>
    public const bool CompTipsByDefault = false;

    /// <summary>Numbers the server fetched longer ago than this are marked as out of date in the tab's line.</summary>
    public static readonly TimeSpan BgStaleAfter = TimeSpan.FromDays(14);

    private const string LoadingGlyph = "\U000F051F";
    private const string NoticeGlyph = "\U000F002A";
    private static readonly IReadOnlyDictionary<string, string> NoPersonal = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly BgSlice _heroSlice = new(BgSiteClient.HeroesSoloSlices.Default);
    private readonly BgSlice _trinketSlice = new(BgSiteClient.TrinketSlices.Default);
    private BgSiteStats? _bgSite;
    private bool _bgSiteLoaded;
    private string _bgTab = "Mine";
    private bool _showCompTips = CompTipsByDefault;
    private IReadOnlyDictionary<string, string> _bgPersonalSolo = NoPersonal;
    private IReadOnlyDictionary<string, string> _bgPersonalDuos = NoPersonal;
    private BgTabState _heroTab = Pending();
    private BgTabState _trinketTab = Pending();
    private BgTabState _cardTab = Pending();
    private BgTabState _compTab = Pending();
    private string _heroTopLabel = Loc.T("Main.Table.Top4");
    private string _heroTopTip = Loc.T("Main.Table.Top4Tip");
    private bool _cardStatsShown;

    /// <summary>The heroes: a tier's head, then its heroes; sorting moves heroes inside their tier.</summary>
    public SyncList<object> HeroTierRows { get; } = [];

    public SyncList<object> TrinketTierRows { get; } = [];

    /// <summary>The minions of the chosen tavern tier, or the tavern spells (<see cref="CardTier"/> 0).</summary>
    public SyncList<object> CardTierRows { get; } = [];

    /// <summary>The comps by tier; or, when the service has none and the pool has tribes, the tribes without heads.</summary>
    public SyncList<object> CompTiers { get; } = [];

    public BgTabState HeroTab { get => _heroTab; private set => Set(ref _heroTab, value); }

    public BgTabState TrinketTab { get => _trinketTab; private set => Set(ref _trinketTab, value); }

    public BgTabState CardTab { get => _cardTab; private set => Set(ref _cardTab, value); }

    public BgTabState CompTab { get => _compTab; private set => Set(ref _compTab, value); }

    /// <summary>The head of the heroes' "top" column: "Top 4", or "Top 2" while a Duos list is shown.</summary>
    public string HeroTopLabel { get => _heroTopLabel; private set => Set(ref _heroTopLabel, value); }

    public string HeroTopTip { get => _heroTopTip; private set => Set(ref _heroTopTip, value); }

    /// <summary>The card list's three stat columns show only when a row of the shown list has numbers.</summary>
    public bool CardStatsShown { get => _cardStatsShown; private set => Set(ref _cardStatsShown, value); }

    /// <summary>
    /// "Solo / Duos" is shown where it changes what is on the page: on My stats, and on Heroes when the service has a
    /// Duos list.
    /// </summary>
    public bool ShowBgMode => _bgTab == "Mine" || (_bgTab == "Heroes" && _bgSite?.HeroesDuos is not null);

    /// <summary>How the tabs ask the service for a slice; the hosts set it once, before the first load.</summary>
    public BgSiteSliceLoader? BgSiteSlices { get; set; }

    /// <summary>Whether the comps show their tip text; <see cref="CompTipsByDefault"/> unless a test turns it.</summary>
    internal bool ShowCompTips
    {
        get => _showCompTips;
        set
        {
            _showCompTips = value;
            RebuildCompRows();
        }
    }

    /// <summary>Now (UTC), for the age of the numbers; tests set it.</summary>
    internal Func<DateTime> BgClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>True while the Duos list is the heroes list to show: Duos is chosen and the service has the list.</summary>
    private bool HeroDuos => BgMode == "Duos" && _bgSite?.HeroesDuos is not null;

    /// <summary>
    /// The lists of the site's service after a load; null when nothing could be had (no network and no cache). The
    /// hosts call it on the UI thread after every refresh. A slice the player picked that is not the default one is
    /// asked for again; its rows stay until the answer.
    /// </summary>
    public void SetBgSiteStats(BgSiteStats? stats)
    {
        _bgSite = stats;
        _bgSiteLoaded = true;
        ShowHeroes(quiet: true);
        ShowTrinkets(quiet: true);
        RebuildCardRows();
        RebuildCompRows();
        RebuildBgPicker();
        Raise(nameof(ShowBgMode));
    }

    /// <summary>The open tab of the page ("Mine", "Heroes", "Comps", "Trinkets", "Cards"): the pickers follow it.</summary>
    public void BgTabShown(string tab)
    {
        if (_bgTab == tab)
        {
            return;
        }

        _bgTab = tab;
        RebuildBgPicker();
        Raise(nameof(ShowBgMode));
    }

    /// <summary>The picker's choice (a percent: 50, 20, 5, 1) for the open tab's list.</summary>
    private void SelectBgSlice(int percent)
    {
        var mmr = string.Create(CultureInfo.InvariantCulture, $"TOP_{percent}_PERCENT");
        if (_bgTab == "Heroes" && BgSiteClient.HeroSlices(HeroDuos).Has(mmr))
        {
            _heroSlice.Mmr = mmr;
            ShowHeroes(quiet: false);
        }
        else if (_bgTab == "Trinkets" && BgSiteClient.TrinketSlices.Has(mmr))
        {
            _trinketSlice.Mmr = mmr;
            ShowTrinkets(quiet: false);
        }

        RebuildBgPicker();
    }

    /// <summary>"Solo / Duos" changed: the heroes list, its slices and the "You" column follow.</summary>
    private void BgModeChanged()
    {
        ShowHeroes(quiet: false);
        RebuildBgPicker();
    }

    /// <summary>The player's own games by hero ("12 · 3.90") for the "You" column, per mode.</summary>
    private void SetBgPersonal(string mode, IReadOnlyList<HeroAggregate> heroes, IReadOnlyList<HeroAggregate>? solo)
    {
        static IReadOnlyDictionary<string, string> Texts(IReadOnlyList<HeroAggregate> list) =>
            list.GroupBy(h => h.HeroCardId, StringComparer.Ordinal).ToDictionary(
                g => g.Key,
                g => $"{g.First().Games.ToString(CultureInfo.CurrentCulture)} · {g.First().AveragePlace.ToString("0.00", CultureInfo.CurrentCulture)}",
                StringComparer.Ordinal);

        if (mode == "Duos")
        {
            _bgPersonalDuos = Texts(heroes);
            _bgPersonalSolo = solo is null ? _bgPersonalSolo : Texts(solo);
        }
        else
        {
            _bgPersonalSolo = Texts(heroes);
        }

        RebuildHeroRows();
    }

    /// <summary>Every tab again from what is loaded: the card database or the language changed.</summary>
    private void RebuildBgTabs()
    {
        RebuildHeroRows();
        RebuildTrinketRows();
        RebuildCardRows();
        RebuildCompRows();
        RebuildBgPicker();
    }

    // ---- slices ---------------------------------------------------------------------------------

    /// <param name="quiet">A planned refresh: no "loading" in the line, the rows stay until the slice answers.</param>
    private void ShowHeroes(bool quiet)
    {
        var duos = HeroDuos;
        var slices = BgSiteClient.HeroSlices(duos);
        var slice = _heroSlice;
        if (!slices.Has(slice.Mmr))
        {
            // The new mode has no such slice: the choice becomes the default one, and stays it.
            slice.Mmr = slices.Default;
        }

        var basis = duos ? _bgSite?.HeroesDuos : _bgSite?.Heroes;
        var mmr = slice.Mmr;
        ShowSlice(slice, basis, slices.Default, duos, quiet, BgSiteSlices is { } loader ? () => loader.Heroes(duos, mmr) : null, RebuildHeroRows);
    }

    private void ShowTrinkets(bool quiet)
    {
        var slice = _trinketSlice;
        var mmr = slice.Mmr;
        ShowSlice(slice, _bgSite?.Trinkets, BgSiteClient.TrinketSlices.Default, duos: false, quiet, BgSiteSlices is { } loader ? () => loader.Trinkets(mmr) : null, RebuildTrinketRows);
    }

    /// <summary>
    /// Puts the chosen slice of a list on screen. The default slice is the refresh's own list (<paramref name="basis"/>),
    /// at once and without a request; another one is asked for, the shown rows stay meanwhile, and the last choice wins.
    /// Without the default list the tab has nothing to show, whatever is chosen.
    /// </summary>
    private void ShowSlice(BgSlice slice, BgSiteTierList? basis, string defaultMmr, bool duos, bool quiet, Func<Task<BgSiteTierList?>>? load, Action rebuild)
    {
        var request = ++slice.Request;
        if (basis is null || slice.Mmr == defaultMmr)
        {
            slice.Show(basis, duos, missing: false);
        }
        else if (load is null)
        {
            slice.Show(null, duos, missing: true);
        }
        else
        {
            // A planned refresh says nothing while rows (or "no data for the slice") are on screen; with nothing on
            // screen - the lists came back after a load that had none - the tab says it is loading.
            if (!quiet || (slice.Shown is null && !slice.Missing))
            {
                slice.Loading = slice.Mmr;
                slice.LoadingDuos = duos;
                slice.Missing = false;
            }

            LoadSliceAsync(slice, request, duos, load, rebuild).Forget("Battlegrounds slice load");
        }

        rebuild();
    }

    private static async Task LoadSliceAsync(BgSlice slice, int request, bool duos, Func<Task<BgSiteTierList?>> load, Action rebuild)
    {
        BgSiteTierList? list = null;
        try
        {
            list = await load().ConfigureAwait(true);
        }
        finally
        {
            // An answer to a choice the player has since changed is dropped. No list is "no data for the slice",
            // whatever the reason (a loader that threw included): the tab does not stay "loading", and the earlier
            // rows do not stay under another slice's name.
            if (request == slice.Request)
            {
                slice.Show(list, duos, missing: list is null);
                rebuild();
            }
        }
    }

    /// <summary>
    /// The picker of the open tab: the slices of its list, widest first, with the chosen one marked. Empty (hidden)
    /// on the other tabs, with a single slice, and while the tab's default list is not loaded.
    /// </summary>
    private void RebuildBgPicker()
    {
        var (slices, chosen) = _bgTab switch
        {
            "Heroes" when (HeroDuos ? _bgSite?.HeroesDuos : _bgSite?.Heroes) is not null => (BgSiteClient.HeroSlices(HeroDuos), _heroSlice.Mmr),
            "Trinkets" when _bgSite?.Trinkets is not null => (BgSiteClient.TrinketSlices, _trinketSlice.Mmr),
            _ => (null, string.Empty),
        };
        BracketOptions = slices is not { Mmr.Count: > 1 }
            ? []
            : slices.Mmr
                .Select(mmr => (Mmr: mmr, Share: SliceShare(mmr)))
                .Where(s => s.Share is not null)
                .Select((s, i) => new BracketOption(
                    (int)Math.Round(s.Share!.Value * 100),
                    i == 0 ? Loc.F("Main.Bracket.Top", s.Share.Value) : s.Share.Value.ToString("P0", CultureInfo.CurrentCulture),
                    s.Mmr == chosen,
                    SetBracket))
                .ToList();
    }

    // ---- shared pieces --------------------------------------------------------------------------

    /// <summary>
    /// A tab's flat list: for each tier of the list, its head and the shown rows of the tier. A tier that is empty in
    /// the service's own list keeps its head, marked empty; one that only our filter (the tavern tier) emptied is left
    /// out. Nothing shown is an empty list (the tab's empty state).
    /// </summary>
    private static List<object> Flat<TRow>(
        BgSiteTierList list,
        IReadOnlyList<(BgSiteTierEntry Entry, TRow Row)> shown,
        string countKey,
        Func<IReadOnlyList<(BgSiteTierEntry Entry, TRow Row)>, IEnumerable<(BgSiteTierEntry Entry, TRow Row)>>? sort = null)
        where TRow : notnull
    {
        var flat = new List<object>();
        if (shown.Count == 0)
        {
            return flat;
        }

        // The service's tiers in its order; a letter only the entries have still gets its head.
        var tiers = list.AvailableTiers.Concat(list.Entries.Select(e => e.Tier)).Distinct(StringComparer.Ordinal);
        foreach (var tier in tiers)
        {
            var rows = shown.Where(r => r.Entry.Tier == tier).ToList();
            if (rows.Count > 0)
            {
                flat.Add(Head(tier, Loc.P(countKey, rows.Count), empty: false));
                flat.AddRange((sort?.Invoke(rows) ?? rows).Select(r => (object)r.Row));
            }
            else if (!list.Entries.Any(e => e.Tier == tier))
            {
                flat.Add(Head(tier, Loc.T("Main.Bg.TierEmpty"), empty: true));
            }
        }

        return flat;
    }

    private static TierHeadRow Head(string tier, string caption, bool empty) => new(tier, Loc.F("Main.Meta.TierTitle", tier), caption, empty);

    private static BgTabState Pending() => Empty(Loc.T("Main.Bg.CommunityLoading"));

    private static BgTabState Rows(string source) => new(true, source, string.Empty, NoticeGlyph);

    private static BgTabState Empty(string text) =>
        new(false, string.Empty, text, text == Loc.T("Main.Bg.CommunityLoading") ? LoadingGlyph : NoticeGlyph);

    /// <summary>Why no tab has rows: nothing loaded yet, or the load had nothing; null once there are lists.</summary>
    private string? WholeEmptyText() =>
        !_bgSiteLoaded ? Loc.T("Main.Bg.CommunityLoading") : _bgSite is null ? Loc.T("Main.Bg.CommunityUnavailable") : null;

    private string SliceEmptyText(BgSlice slice) =>
        WholeEmptyText() ?? (slice.Missing && SliceShare(slice.Mmr) is { } share
            ? Loc.F("Main.Bg.SliceEmpty", share)
            : Loc.T(slice.Loading is not null ? "Main.Bg.CommunityLoading" : "Main.Bg.ListUnavailable"));

    /// <summary>
    /// "Hearthpulse data · Solo · top 50 % by MMR · current patch · updated 09 Nov 23:06": the source, then what the
    /// shown list itself says about its slice (a part it does not name is left out), then when the server fetched it.
    /// While another slice loads, the date gives way to "loading top 5 %…" - for heroes with the mode asked for
    /// ("loading Duos, top 20 %…"), which is what changes when the player switches Solo / Duos; numbers older than
    /// <see cref="BgStaleAfter"/> are marked.
    /// </summary>
    private string BgSource(DateTime? fetched, string? loading, string? loadingMode, params string?[] parts)
    {
        var what = Joined([HearthpulseData.Attribution, .. parts]);
        if (loading is not null && SliceShare(loading) is { } share)
        {
            return loadingMode is null ? Loc.F("Main.Bg.SliceLoading", what, share) : Loc.F("Main.Bg.SliceLoadingMode", what, loadingMode, share);
        }

        if (fetched is not { } at || at == default)
        {
            return what;
        }

        var line = Loc.F("Main.Bg.MetaUpdated", what, at.ToLocalTime());
        return BgClock() - at.ToUniversalTime() > BgStaleAfter ? line + " · " + Loc.T("Main.Bg.Stale") : line;
    }

    /// <summary>"TOP_50_PERCENT" as a share (0.5); null for anything else.</summary>
    private static double? SliceShare(string? mmr) =>
        mmr is not null && mmr.StartsWith("TOP_", StringComparison.Ordinal) && mmr.EndsWith("_PERCENT", StringComparison.Ordinal)
        && int.TryParse(mmr.AsSpan(4, mmr.Length - 12), NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent is > 0 and <= 100
            ? percent / 100.0
            : null;

    /// <summary>The slice as the player reads it; a value the app does not know is left out, never shown as code.</summary>
    private static string? SliceText(string? mmr) => SliceShare(mmr) is { } share ? Loc.F("Main.Bg.SliceMmr", share) : null;

    private static string? RangeText(string? range) =>
        range == "CURRENT_BATTLEGROUNDS_PATCH" ? Loc.T("Main.Bg.RangePatch")
        : range is not null && range.StartsWith("LAST_", StringComparison.Ordinal) && range.EndsWith("_DAYS", StringComparison.Ordinal)
          && int.TryParse(range.AsSpan(5, range.Length - 10), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0
            ? Loc.P("Main.Bg.RangeDays", days)
            : null;

    /// <summary>The card's name in the card database's language; the service's own name when the database lacks the card.</summary>
    private string BgName(BgSiteTierEntry entry) =>
        _cards.Get(entry.CardId)?.Name is { Length: > 0 } name
            ? name
            : (Loc.IsRussian ? First(entry.NameRu, entry.NameEn) : First(entry.NameEn, entry.NameRu)) is { Length: > 0 } own ? own : entry.CardId;

    private static string First(string? first, string? second) => !string.IsNullOrEmpty(first) ? first : second ?? string.Empty;

    private static string Number(double? value, string format) => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";

    private static string Joined(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>
    /// The chosen slice of a list and what is on screen for it. <see cref="Loading"/> is the slice asked for by the
    /// player and not answered yet; <see cref="Missing"/>: the chosen slice came back without data.
    /// </summary>
    private sealed class BgSlice(string mmr)
    {
        public string Mmr { get; set; } = mmr;

        public BgSiteTierList? Shown { get; private set; }

        public bool ShownDuos { get; private set; }

        public string? Loading { get; set; }

        /// <summary>The mode of the heroes slice that is loading.</summary>
        public bool LoadingDuos { get; set; }

        public bool Missing { get; set; }

        public int Request { get; set; }

        public void Show(BgSiteTierList? list, bool duos, bool missing)
        {
            Shown = list;
            ShownDuos = duos;
            Loading = null;
            Missing = missing;
        }
    }
}
