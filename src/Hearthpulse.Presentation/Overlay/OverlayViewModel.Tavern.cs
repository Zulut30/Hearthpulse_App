using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;

namespace Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The tavern bar's tribe chips: the lobby's tribes, each a filter for the tier list under the bar (one tribe at a
/// time; the same chip again shows every tribe).
/// </summary>
public sealed partial class OverlayViewModel
{
    private string _selectedTribe = string.Empty;
    private IReadOnlyList<TribeChip> _tribeChips = Array.Empty<TribeChip>();
    private string _browserTitle = string.Empty;
    private string _browserScope = string.Empty;
    private RelayCommand? _selectTribe;

    public IReadOnlyList<TribeChip> TribeChips { get => _tribeChips; private set => Set(ref _tribeChips, value); }

    /// <summary>The open tier list's heading ("Tier 4").</summary>
    public string BrowserTitle { get => _browserTitle; private set => Set(ref _browserTitle, value); }

    /// <summary>What the tier list shows: the minions of the lobby, or of the one tribe chosen on the bar.</summary>
    public string BrowserScope { get => _browserScope; private set => Set(ref _browserScope, value); }

    /// <summary>
    /// Shows only one tribe in the tier list (the tribe key as parameter); the same tribe again shows them all. With
    /// the list closed it opens at the player's tavern tier.
    /// </summary>
    public RelayCommand SelectTribe => _selectTribe ??= new RelayCommand(p =>
    {
        var key = p as string ?? string.Empty;
        _selectedTribe = _selectedTribe == key ? string.Empty : key;
        if (SelectedTier == 0 && _selectedTribe.Length > 0 && _bg is { } bg)
        {
            SelectedTier = Math.Max(1, bg.TechLevel);
            RebuildTiers();
        }

        RebuildTribeChips();
        RebuildBrowser();
    });

    private void RebuildTribeChips()
    {
        var races = _bg?.Races ?? [];
        // A new lobby without the chosen tribe forgets the filter.
        if (_selectedTribe.Length > 0 && races.Count > 0 && !races.Contains(_selectedTribe))
        {
            _selectedTribe = string.Empty;
        }

        TribeChips = races.Select(r => new TribeChip(r, Races.Display(r, _locale), r == _selectedTribe)).ToList();
    }

    /// <summary>Whether a minion with these tribes belongs in the list while a tribe chip is chosen.</summary>
    private bool InChosenTribe(IReadOnlyList<string> races) =>
        _selectedTribe.Length == 0 || races.Contains(_selectedTribe) || races.Contains("ALL");

    private void UpdateBrowserHeading()
    {
        BrowserTitle = SelectedTier == 0 ? string.Empty : Loc.F("Overlay.Browser.Title", SelectedTier);
        BrowserScope = _selectedTribe.Length > 0 ? Races.Display(_selectedTribe, _locale) : Loc.T("Overlay.Browser.MinionsInLobby");
    }
}
