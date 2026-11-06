using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Decks;
#else
namespace Hearthpulse.UI.Main.Decks;
#endif

/// <summary>
/// The deck page: one deck family's results, matchups, personal mulligan, card performance, versions and recent
/// games. A selected matchup filters every block but the matchups. Family changes (merge, split, rename, main
/// version) go to the history and the page is shown again from the reloaded families. The Versions card's text comes
/// from <see cref="DeckVersionsText"/>.
/// </summary>
public sealed partial class DeckDetailsViewModel : Observable
{
    /// <summary>Bars of the mana curve: 0 … 6 and 7+.</summary>
    private const int CurveBuckets = 8;

    private readonly MainViewModel _owner;
    private DeckFamily? _family;
    private IReadOnlyList<DeckFamily> _families = [];
    private IReadOnlyList<MergeSuggestion> _suggestions = [];
    private IReadOnlySet<(string, string)> _dismissed = new HashSet<(string, string)>();
    private readonly HashSet<string> _openDiffs = new(StringComparer.Ordinal);
    private ConstructedMeta? _meta;
    private CardDb _cards = CardDb.Empty;
    private string? _matchup;
    private int _build;

    private string _name = string.Empty;
    private string _editName = string.Empty;
    private string _heroCardId = string.Empty;
    private IReadOnlyList<string> _facts = [];
    private string _lastPlayed = string.Empty;
    private string _winRateText = "—";
    private string _winRecord = string.Empty;
    private int _winSign;
    private double _winScale = -1;
    private IReadOnlyList<ManaCurveBar> _curve = [];
    private IReadOnlyList<DeckListRow> _deckList = [];
    private string _deckListNote = string.Empty;
    private string _archetypeLine = string.Empty;
    private string _filterText = string.Empty;
    private IReadOnlyList<StatTile> _tiles = [];
    private IReadOnlyList<StatTile> _moreTiles = [];
    private bool _mulliganAllColumns;
    private bool _cardsAllColumns;
    private IReadOnlyList<IChartValue> _chart = [];
    private IReadOnlyList<int> _lengthWins = [];
    private IReadOnlyList<int> _lengthLosses = [];
    private IReadOnlyList<string> _lengthLabels = [];
    private IReadOnlyList<DeckMatchupRow> _matchups = [];
    private IReadOnlyList<DeckCardStatRow> _cardRows = [];
    private IReadOnlyList<DeckCardStatRow> _mulliganRows = [];
    private IReadOnlyList<DeckCardStatRow> _performanceRows = [];
    private string _mulliganNote = string.Empty;
    private string _cardsNote = string.Empty;
    private IReadOnlyList<DeckVersionRow> _versions = [];
    private IReadOnlyList<DeckSuggestionRow> _suggestionRows = [];
    private bool _renaming;
    private string _undoText = string.Empty;
    private bool _pickerOpen;
    private string _search = string.Empty;
    private string? _chosen;
    private IReadOnlyList<MergeCandidateRow> _candidates = [];
    private string _candidatesEmpty = string.Empty;
    private IReadOnlyList<DeckGameRow> _games = [];

    public DeckDetailsViewModel(MainViewModel owner)
    {
        _owner = owner;
        Back = new RelayCommand(owner.CloseDeck);
        SelectMatchup = new RelayCommand(p =>
        {
            var key = p as string;
            _matchup = key == _matchup ? null : key;
            Rebuild();
        });
        ClearMatchup = new RelayCommand(() =>
        {
            _matchup = null;
            Rebuild();
        });
        MatchupSort = new TableSort("Games", Rebuild, "Name");
        MulliganSort = new TableSort("Card", () => MulliganRows = MulliganSort!.Apply(_cardRows, (r, c) => r.SortValue(c)), "Card");
        CardsSort = new TableSort("Card", () => PerformanceRows = CardsSort!.Apply(_cardRows, (r, c) => r.SortValue(c)), "Card");
        StartRename = new RelayCommand(() =>
        {
            EditName = _family?.Name ?? string.Empty;
            Renaming = true;
        });
        CancelRename = new RelayCommand(() => Renaming = false);
        Rename = new RelayCommand(() =>
        {
            Renaming = false;
            _owner.RenameDeck(_family, EditName);
        });
        SetMain = new RelayCommand(p => _owner.SetMainVersion(_family, p as string));
        Split = new RelayCommand(p => _owner.SplitVersion(_family, p as string));
        ToggleDiff = new RelayCommand(p =>
        {
            if (p is string key && !_openDiffs.Remove(key))
            {
                _openDiffs.Add(key);
            }

            ShowOpenDiffs(Versions, Suggestions);
        });
        Merge = new RelayCommand(p => _owner.MergeDecks(p as DeckSuggestionRow));
        Dismiss = new RelayCommand(p => _owner.DismissDecks(p as DeckSuggestionRow));
        UndoMerge = new RelayCommand(_owner.UndoMerge);
        TogglePicker = new RelayCommand(() =>
        {
            PickerOpen = !PickerOpen;
            _chosen = null;
            _search = string.Empty;
            Raise(nameof(Search));
            RefreshCandidates();
        });
        Choose = new RelayCommand(p =>
        {
            _chosen = p as string;
            RefreshCandidates();
        });
        AddChosen = new RelayCommand(() =>
        {
            if (_family is not null && _chosen is { } key)
            {
                PickerOpen = false;
                _owner.MergeDecks(new DeckSuggestionRow(_family.Key, key, string.Empty, string.Empty));
            }
        });
        OpenGame = new RelayCommand(p =>
        {
            if (p is string key)
            {
                _owner.ShowHistoryGame(key);
            }
        });
    }

    public RelayCommand Back { get; }

    public RelayCommand SelectMatchup { get; }

    public RelayCommand ClearMatchup { get; }

    /// <summary>The matchups' column heads: "Name", "Games", "WinRate"; archetypes stay under their class.</summary>
    public TableSort MatchupSort { get; }

    /// <summary>
    /// The mulligan table's column heads: "Card" (cost, then name), "Offered", "Kept", "KeptWinRate",
    /// "ReplacedWinRate", "MetaKept", "MetaWinRate".
    /// </summary>
    public TableSort MulliganSort { get; }

    /// <summary>The card table's column heads: "Card", "Drawn", "DrawnWinRate", "Played", "PlayedWinRate", "AverageTurn".</summary>
    public TableSort CardsSort { get; }

    /// <summary>Saves <see cref="EditName"/> as the deck's name.</summary>
    public RelayCommand Rename { get; }

    public RelayCommand StartRename { get; }

    public RelayCommand CancelRename { get; }

    public RelayCommand SetMain { get; }

    public RelayCommand Split { get; }

    public RelayCommand Merge { get; }

    /// <summary>"Not the same deck": the pair is never suggested again.</summary>
    public RelayCommand Dismiss { get; }

    /// <summary>Shows or hides the cards of a version (its key) or of a suggested deck (its key) against the main version.</summary>
    public RelayCommand ToggleDiff { get; }

    public RelayCommand UndoMerge { get; }

    /// <summary>Opens or closes the list of decks to add to this family.</summary>
    public RelayCommand TogglePicker { get; }

    /// <summary>Picks a deck in that list (its key).</summary>
    public RelayCommand Choose { get; }

    /// <summary>Adds the picked deck to this family.</summary>
    public RelayCommand AddChosen { get; }

    public RelayCommand OpenGame { get; }

    /// <summary>The family key of the deck shown.</summary>
    public string? Key => _family?.Key;

    public string Name { get => _name; private set => Set(ref _name, value); }

    /// <summary>The name box; <see cref="Rename"/> saves it (blank goes back to the main version's name).</summary>
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    /// <summary>True while the name box shows in place of the name.</summary>
    public bool Renaming { get => _renaming; private set => Set(ref _renaming, value); }

    public string HeroCardId { get => _heroCardId; private set => Set(ref _heroCardId, value); }

    /// <summary>The header chips: class, format and "3 versions" when there are more than one.</summary>
    public IReadOnlyList<string> Facts { get => _facts; private set => Set(ref _facts, value); }

    /// <summary>"Last played 2 h ago".</summary>
    public string LastPlayed { get => _lastPlayed; private set => Set(ref _lastPlayed, value); }

    /// <summary>The big win rate of the header (the games shown, so a matchup filter applies), "—" without games.</summary>
    public string WinRateText { get => _winRateText; private set => Set(ref _winRateText, value); }

    /// <summary>"12–8 · 20 games" under the big win rate.</summary>
    public string WinRecord { get => _winRecord; private set => Set(ref _winRecord, value); }

    /// <summary>The big win rate's colour: +1 from 53 %, -1 up to 47 %, else 0.</summary>
    public int WinSign { get => _winSign; private set => Set(ref _winSign, value); }

    /// <summary>
    /// Where the win rate sits on the header scale, 0..1 over <see cref="ScaleLow"/>..<see cref="ScaleHigh"/> (clamped);
    /// -1 without games (no marker).
    /// </summary>
    public double WinScale { get => _winScale; private set { if (Set(ref _winScale, value)) { Raise(nameof(HasWinScale)); } } }

    public bool HasWinScale => _winScale >= 0;

    /// <summary>The header scale runs from 30 % to 70 %: a deck rarely leaves it, and 47–53 % sits in its middle.</summary>
    public const double ScaleLow = 0.30;

    public const double ScaleHigh = 0.70;

    /// <summary>The main version's mana curve, 0 to 7+.</summary>
    public IReadOnlyList<ManaCurveBar> Curve { get => _curve; private set => Set(ref _curve, value); }

    /// <summary>The main version's cards by cost, then name.</summary>
    public IReadOnlyList<DeckListRow> DeckList { get => _deckList; private set => Set(ref _deckList, value); }

    /// <summary>"30 cards · average cost 3.4", empty without a list.</summary>
    public string DeckListNote { get => _deckListNote; private set => Set(ref _deckListNote, value); }

    /// <summary>"Blood DK · 54% in the meta", empty when the deck matches no archetype.</summary>
    public string ArchetypeLine { get => _archetypeLine; private set => Set(ref _archetypeLine, value); }

    /// <summary>"Only games vs Priest (6)" while a matchup is selected, else empty.</summary>
    public string FilterText { get => _filterText; private set => Set(ref _filterText, value); }

    /// <summary>Games, average game and the win rate against the meta: with the banner's win rate, the page's four numbers.</summary>
    public IReadOnlyList<StatTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }

    /// <summary>Going first and on the coin, under «Подробнее» with the charts.</summary>
    public IReadOnlyList<StatTile> MoreTiles { get => _moreTiles; private set => Set(ref _moreTiles, value); }

    /// <summary>The mulligan table's rarely read columns (win rate if replaced, the meta's keep rate and win rate); off by default.</summary>
    public bool MulliganAllColumns { get => _mulliganAllColumns; set => Set(ref _mulliganAllColumns, value); }

    /// <summary>The card table's rarely read columns (played, win rate if played, average turn); off by default.</summary>
    public bool CardsAllColumns { get => _cardsAllColumns; set => Set(ref _cardsAllColumns, value); }

    public IReadOnlyList<IChartValue> Chart { get => _chart; private set => Set(ref _chart, value); }

    public IReadOnlyList<int> LengthWins { get => _lengthWins; private set => Set(ref _lengthWins, value); }

    public IReadOnlyList<int> LengthLosses { get => _lengthLosses; private set => Set(ref _lengthLosses, value); }

    public IReadOnlyList<string> LengthLabels { get => _lengthLabels; private set => Set(ref _lengthLabels, value); }

    public IReadOnlyList<DeckMatchupRow> Matchups { get => _matchups; private set => Set(ref _matchups, value); }

    public bool SortedByWinRate => MatchupSort.Column == "WinRate";

    /// <summary>The cards of both tables in the deck's order (cost, then name).</summary>
    public IReadOnlyList<DeckCardStatRow> CardRows { get => _cardRows; private set => Set(ref _cardRows, value); }

    /// <summary>The mulligan table: <see cref="CardRows"/> in <see cref="MulliganSort"/> order.</summary>
    public IReadOnlyList<DeckCardStatRow> MulliganRows { get => _mulliganRows; private set => Set(ref _mulliganRows, value); }

    /// <summary>The card table: <see cref="CardRows"/> in <see cref="CardsSort"/> order.</summary>
    public IReadOnlyList<DeckCardStatRow> PerformanceRows { get => _performanceRows; private set => Set(ref _performanceRows, value); }

    public string MulliganNote { get => _mulliganNote; private set => Set(ref _mulliganNote, value); }

    public string CardsNote { get => _cardsNote; private set => Set(ref _cardsNote, value); }

    public IReadOnlyList<DeckVersionRow> Versions { get => _versions; private set => Set(ref _versions, value); }

    public IReadOnlyList<DeckSuggestionRow> Suggestions { get => _suggestionRows; private set => Set(ref _suggestionRows, value); }

    /// <summary>"Merged “Рыцарь смерти2” into this deck" right after a merge into the deck shown (with Undo), else empty.</summary>
    public string UndoText { get => _undoText; private set => Set(ref _undoText, value); }

    public bool PickerOpen { get => _pickerOpen; private set => Set(ref _pickerOpen, value); }

    /// <summary>The picker's search box: keeps the decks whose name holds it.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                RefreshCandidates();
            }
        }
    }

    public IReadOnlyList<MergeCandidateRow> Candidates { get => _candidates; private set => Set(ref _candidates, value); }

    /// <summary>Why the picker's list is empty (no other deck of the class and format, or none matches), else empty.</summary>
    public string CandidatesEmpty { get => _candidatesEmpty; private set => Set(ref _candidatesEmpty, value); }

    /// <summary>True once a deck is picked: <see cref="AddChosen"/> needs one.</summary>
    public bool CanAddChosen => _chosen is not null;

    public IReadOnlyList<DeckGameRow> Games { get => _games; private set => Set(ref _games, value); }

    /// <summary>Shows a deck. The matchup filter is kept when the same deck is shown again (after a family change).</summary>
    public void Show(DeckFamily family, IReadOnlyList<DeckFamily> families, IReadOnlyList<MergeSuggestion> suggestions, ConstructedMeta? meta, CardDb cards)
    {
        if (_family?.Key != family.Key)
        {
            _matchup = null;
            EditName = family.Name;
            Renaming = false;
            PickerOpen = false;
            _openDiffs.Clear();
        }

        _family = family;
        _families = families;
        _suggestions = suggestions;
        _meta = meta;
        _cards = cards;
        UndoText = _owner.MergeUndoText(family.Key) ?? string.Empty;
        Raise(nameof(Key));
        Rebuild();
    }

    /// <summary>The rows with the card differences the player opened (kept while the page is rebuilt).</summary>
    private void ShowOpenDiffs(IEnumerable<DeckVersionRow> versions, IEnumerable<DeckSuggestionRow> suggestions)
    {
        Versions = versions.Select(v => v with { DiffOpen = _openDiffs.Contains(v.Key) }).ToList();
        Suggestions = suggestions.Select(s => s with { DiffOpen = _openDiffs.Contains(s.SecondKey) }).ToList();
    }

    /// <summary>The picker's list from the loaded families: same class and format, the search applied, most alike first.</summary>
    private void RefreshCandidates()
    {
        if (_family is not { } family || !PickerOpen)
        {
            _chosen = null;
            Candidates = [];
            CandidatesEmpty = string.Empty;
            Raise(nameof(CanAddChosen));
            return;
        }

        var found = DeckFamilies.Candidates(family, _families, _dismissed, _search);
        if (found.All(c => c.Family.Key != _chosen))
        {
            _chosen = null;
        }

        Candidates = found.Select(c => new MergeCandidateRow(
                c.Family.Key,
                c.Family.Name,
                MainViewModel.ClassHero(c.Family.PlayerClass),
                DeckVersionsText.CandidateDetail(c.Family.Record, MainViewModel.When(c.Family.LastPlayed)),
                DeckVersionsText.SharedShort(c.Overlap),
                c.Dismissed,
                c.Family.Key == _chosen))
            .ToList();
        CandidatesEmpty = found.Count > 0 ? string.Empty
            : string.IsNullOrWhiteSpace(_search) ? DeckVersionsText.NoCandidates(OverlayViewModel.ClassName(family.PlayerClass), FormatName(family.Format))
            : DeckVersionsText.NoMatch(_search.Trim());
        Raise(nameof(CanAddChosen));
    }

    private static string FormatName(string format) => ConstructedMeta.FormatKeyOf(format) is { } key
        ? ConstructedMeta.Label(ConstructedMeta.Formats, key)
        : format.Replace("FT_", string.Empty, StringComparison.Ordinal);

    /// <summary>Rebuilds the page off the UI thread; also a TableSort callback (Action), so the task is let go here.</summary>
    private void Rebuild() => BuildPageAsync().Forget("Deck page build");

    private async Task BuildPageAsync()
    {
        if (_family is not { } family)
        {
            return;
        }

        var build = ++_build;
        var (families, suggestions, meta, cards, matchup) = (_families, _suggestions, _meta, _cards, _matchup);
        var (sortColumn, descending) = (MatchupSort.Column, MatchupSort.Descending);
        try
        {
            var apply = await Task.Run(() => Build(family, families, suggestions, meta, cards, matchup, sortColumn, descending)).ConfigureAwait(true);
            if (build == _build)
            {
                apply();
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Deck page build failed", ex);
        }
    }
}
