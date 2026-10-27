#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>One tier of the hero tier list: "Tier S", its bound ("average place under 3.75 · 7 heroes") and its heroes.</summary>
public sealed record HeroTierGroup(string Tier, string Title, string Caption, IReadOnlyList<MetaHeroRow> Rows);

/// <summary>One tier of the trinket tier list, as <see cref="HeroTierGroup"/>.</summary>
public sealed record TrinketTierGroup(string Tier, string Title, string Caption, IReadOnlyList<MetaTrinketRow> Rows);

/// <summary>The sort arrow of each sortable hero column ("▲", "▼" or empty).</summary>
public sealed record HeroSortArrows(string Name, string Placement, string Top4, string First, string Pick, string Games);

public sealed partial class MainViewModel
{
    private IReadOnlyList<HeroTierGroup> _heroTierGroups = [];
    private IReadOnlyList<TrinketTierGroup> _trinketTierGroups = [];
    private string _heroSort = "Placement";
    private bool _heroSortAscending = true;
    private RelayCommand? _setHeroSort;

    /// <summary>The hero tier list by tier, S to D; tiers without heroes are left out.</summary>
    public IReadOnlyList<HeroTierGroup> HeroTierGroups { get => _heroTierGroups; private set => Set(ref _heroTierGroups, value); }

    public IReadOnlyList<TrinketTierGroup> TrinketTierGroups { get => _trinketTierGroups; private set => Set(ref _trinketTierGroups, value); }

    public HeroSortArrows HeroArrows => new(HeroArrow("Name"), HeroArrow("Placement"), HeroArrow("Top4"), HeroArrow("First"), HeroArrow("Pick"), HeroArrow("Games"));

    /// <summary>
    /// Sorts the heroes inside their tiers by a column ("Name", "Placement", "Top4", "First", "Pick", "Games"); the sorted
    /// column again turns the order round. Placement and names start low to high, the rates high to low.
    /// </summary>
    public RelayCommand SetHeroSort => _setHeroSort ??= new RelayCommand(p =>
    {
        var column = p as string is "Name" or "Placement" or "Top4" or "First" or "Pick" or "Games" ? (string)p : "Placement";
        _heroSortAscending = column == _heroSort ? !_heroSortAscending : column is "Name" or "Placement";
        _heroSort = column;
        Raise(nameof(HeroArrows));
        RebuildHeroGroups();
    });

    private string HeroArrow(string column) => column != _heroSort ? string.Empty : _heroSortAscending ? "▲" : "▼";

    private void RebuildHeroGroups()
    {
        IEnumerable<MetaHeroRow> Sorted(IEnumerable<MetaHeroRow> rows)
        {
            if (_heroSort == "Name")
            {
                return _heroSortAscending ? rows.OrderBy(r => r.Name, StringComparer.CurrentCulture) : rows.OrderByDescending(r => r.Name, StringComparer.CurrentCulture);
            }

            Func<MetaHeroRow, double> key = _heroSort switch
            {
                "Top4" => r => r.Top4Value,
                "First" => r => r.FirstValue,
                "Pick" => r => r.PickValue,
                "Games" => r => r.Games,
                _ => r => r.AverageValue,
            };
            return (_heroSortAscending ? rows.OrderBy(key) : rows.OrderByDescending(key)).ThenBy(r => r.AverageValue);
        }

        HeroTierGroups = Groups(_heroTiers, r => r.Tier, (tier, rows) => new HeroTierGroup(tier, Loc.F("Main.Meta.TierTitle", tier),
            TierCaption(tier, rows.Count, "Main.Bg.HeroTierUnder", "Main.Bg.HeroTierFrom"), Sorted(rows).ToList()));
    }

    private void RebuildTrinketGroups() =>
        TrinketTierGroups = Groups(_trinketTiers, r => r.Tier, (tier, rows) => new TrinketTierGroup(tier, Loc.F("Main.Meta.TierTitle", tier),
            TierCaption(tier, rows.Count, "Main.Bg.TrinketTierUnder", "Main.Bg.TrinketTierFrom"), rows));

    /// <summary>"Average place under 3.75 · 7 heroes"; D, the open-ended tier, says where it starts.</summary>
    private static string TierCaption(string tier, int count, string under, string from) =>
        Loc.P(tier == "D" ? from : under, count, BattlegroundsAdvisor.TierBound(tier).ToString("0.00", CultureInfo.CurrentCulture));

    private static IReadOnlyList<TGroup> Groups<TRow, TGroup>(IReadOnlyList<TRow> rows, Func<TRow, string> tierOf, Func<string, IReadOnlyList<TRow>, TGroup> make) =>
        new[] { "S", "A", "B", "C", "D" }
            .Select(t => (Tier: t, Rows: (IReadOnlyList<TRow>)rows.Where(r => tierOf(r) == t).ToList()))
            .Where(g => g.Rows.Count > 0)
            .Select(g => make(g.Tier, g.Rows))
            .ToList();
}
