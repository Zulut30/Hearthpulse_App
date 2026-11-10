#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>The sort arrow of each sortable hero column ("▲", "▼" or empty).</summary>
public sealed record HeroSortArrows(string Name, string Placement, string Top4, string First, string Pick) : IImmutableView;

public sealed partial class MainViewModel
{
    // No column: the service's order inside each tier, and no arrow.
    private string _heroSort = string.Empty;
    private bool _heroSortAscending = true;
    private RelayCommand? _setHeroSort;
    private bool _bgMetaFailed;

    public HeroSortArrows HeroArrows => new(HeroArrow("Name"), HeroArrow("Placement"), HeroArrow("Top4"), HeroArrow("First"), HeroArrow("Pick"));

    /// <summary>
    /// Sorts the heroes inside their tiers by a column ("Name", "Placement", "Top4", "First", "Pick"). The first click
    /// sorts the natural way (names and places low to high, the rates high to low), the second turns it round, the
    /// third goes back to the service's order. A new slice or mode keeps the sort.
    /// </summary>
    public RelayCommand SetHeroSort => _setHeroSort ??= new RelayCommand(p =>
    {
        if (p as string is not ("Name" or "Placement" or "Top4" or "First" or "Pick"))
        {
            return;
        }

        var column = (string)p;
        var natural = column is "Name" or "Placement";
        if (column != _heroSort)
        {
            _heroSort = column;
            _heroSortAscending = natural;
        }
        else if (_heroSortAscending == natural)
        {
            _heroSortAscending = !natural;
        }
        else
        {
            _heroSort = string.Empty;
        }

        Raise(nameof(HeroArrows));
        RebuildHeroRows();
    });

    /// <summary>
    /// The host's community load returned nothing. With nothing loaded yet My stats says the community numbers are
    /// unavailable; with stats already in, nothing changes.
    /// </summary>
    public void BattlegroundsMetaUnavailable()
    {
        if (_meta is not null)
        {
            return;
        }

        _bgMetaFailed = true;
        RebuildBattlegroundsStats();
    }

    /// <summary>Community data came: My stats no longer says it is unavailable.</summary>
    private void ClearBattlegroundsMetaFailure() => _bgMetaFailed &= _meta is null;

    private string HeroArrow(string column) => column != _heroSort ? string.Empty : _heroSortAscending ? "▲" : "▼";
}
