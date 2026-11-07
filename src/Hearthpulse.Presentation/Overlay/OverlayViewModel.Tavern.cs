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
/// The lobby's tribes: a plain line on the tavern bar ("Tribes: Beast, Dragon"), and chips in the head of the tier list
/// that filter it (one tribe at a time; the same chip again shows every tribe).
/// </summary>
public sealed partial class OverlayViewModel
{
    private string _selectedTribe = string.Empty;
    private IReadOnlyList<TribeChip> _tribeChips = Array.Empty<TribeChip>();
    private string _tribesLine = string.Empty;
    private string _tribesLabel = string.Empty;
    private string _tribesNames = string.Empty;
    private string _browserTitle = string.Empty;
    private string _browserScope = string.Empty;
    private RelayCommand? _selectTribe;

    public IReadOnlyList<TribeChip> TribeChips { get => _tribeChips; private set => Set(ref _tribeChips, value); }

    /// <summary>The bar's second line: the lobby's tribes as text, or that the lobby is still being read.</summary>
    public string TribesLine { get => _tribesLine; private set => Set(ref _tribesLine, value); }

    /// <summary>
    /// <see cref="TribesLine"/> in two parts for the view: the label ("Tribes:", the text of the line's key before its
    /// value) and what follows it (the tribes, or "reading the lobby…").
    /// </summary>
    public string TribesLabel { get => _tribesLabel; private set => Set(ref _tribesLabel, value); }

    public string TribesNames { get => _tribesNames; private set => Set(ref _tribesNames, value); }

    /// <summary>The open tier list's heading ("Tier 4").</summary>
    public string BrowserTitle { get => _browserTitle; private set => Set(ref _browserTitle, value); }

    /// <summary>What the tier list shows: the minions of the lobby, or of the one tribe chosen in its head.</summary>
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
        var names = string.Join(", ", races.Select(r => Races.Display(r, _locale)));
        TribesLine = races.Count == 0 ? Loc.T("Overlay.Bar.TribesReading") : Loc.F("Overlay.Bar.TribesLine", names);

        // The label is the line's own text up to its value, so both languages split where their key puts the value.
        var format = Loc.T("Overlay.Bar.TribesLine");
        var at = format.IndexOf("{0}", StringComparison.Ordinal);
        var label = at > 0 ? format[..at].TrimEnd() : string.Empty;
        var rest = races.Count == 0 ? TribesLine : names;
        if (races.Count == 0)
        {
            rest = label.Length > 0 && rest.StartsWith(label, StringComparison.Ordinal) ? rest[label.Length..].TrimStart() : rest;
            label = rest == TribesLine ? string.Empty : label;
        }

        TribesLabel = label;
        TribesNames = rest;
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
