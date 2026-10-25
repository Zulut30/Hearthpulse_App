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

/// <summary>A game on the deck's win-rate line, with what its hover card shows.</summary>
public sealed record DeckGamePoint(
    double ChartValue,
    string ChartLabel,
    string Result,
    string Title,
    string Detail,
    string When,
    string SoFar,
    string OpponentHeroCardId) : IChartValue
{
    /// <summary>The result as the player reads it; <see cref="Result"/> keeps the stored value for the colour.</summary>
    public string ResultText => DeckDetailsViewModel.ResultText(Result);
}

/// <summary>A matchup: an opponent class, or an archetype of it (indented under the classes).</summary>
public sealed record DeckMatchupRow(
    string Key,
    string Label,
    string HeroCardId,
    string Games,
    string Record,
    string WinRate,
    double WinFraction,
    bool IsArchetype,
    bool Selected,
    bool Few);

/// <summary>One card in the mulligan and card tables. Signs colour a win rate against the deck's own (+1 above, -1 below).</summary>
public sealed record DeckCardStatRow(
    string CardId,
    string Name,
    int Cost,
    string Copies,
    bool InMain,
    bool FewMulligan,
    bool FewDrawn,
    string Offered,
    string KeepRate,
    string KeptWinRate,
    int KeptSign,
    string ReplacedWinRate,
    int ReplacedSign,
    string MetaKeep,
    string MetaWinRate,
    string Drawn,
    string DrawnWinRate,
    int DrawnSign,
    string Played,
    string PlayedWinRate,
    int PlayedSign,
    string AverageTurn);

/// <summary>A card whose copies differ between two lists: its tile, name and "+2" / "−1" (the sign colours it).</summary>
public sealed record CardDiffRow(string CardId, string Name, string Count, int Sign);

/// <summary>
/// A version of the deck with its own results and its cards against the main version. <see cref="Few"/>: too few games
/// to tell, the win bar is faded. <see cref="DiffOpen"/>: its cards against the main version are shown.
/// </summary>
public sealed record DeckVersionRow(
    string Key,
    string Name,
    bool IsMain,
    string Detail,
    string Record,
    string WinRate,
    double WinFraction,
    int Games,
    bool Few,
    string Changes,
    IReadOnlyList<CardDiffRow> Diff,
    bool DiffOpen,
    bool CanManage)
{
    public bool CanMakeMain => CanManage && !IsMain;

    public bool HasDiff => Diff.Count > 0;

    public string DiffAction => DiffOpen ? DeckVersionsText.HideDiff : DeckVersionsText.ShowDiff;

    public string FewGamesTip => DeckVersionsText.FewGamesTip;
}

/// <summary>
/// Another deck that looks like a version of this one. On the deck page it also has its cards against this deck's main
/// version (<see cref="Changes"/> "+2 / −2 cards", <see cref="Diff"/>), shown while <see cref="DiffOpen"/>.
/// </summary>
public sealed record DeckSuggestionRow(string FirstKey, string SecondKey, string Title, string Detail)
{
    public string Changes { get; init; } = string.Empty;

    public IReadOnlyList<CardDiffRow> Diff { get; init; } = [];

    public bool DiffOpen { get; init; }
}

/// <summary>A deck of the same class and format that can be added to this one by hand (most alike first).</summary>
public sealed record MergeCandidateRow(string Key, string Name, string HeroCardId, string Detail, string Shared, bool Dismissed, bool Selected);

/// <summary>A recent game of the deck.</summary>
public sealed record DeckGameRow(string Key, string When, string Result, string Opponent, string OpponentHeroCardId, string Detail)
{
    /// <summary>The result as the player reads it; <see cref="Result"/> keeps the stored value for the colour.</summary>
    public string ResultText => DeckDetailsViewModel.ResultText(Result);
}

/// <summary>
/// The deck page: one deck family's results, matchups, personal mulligan, card performance, versions and recent
/// games. A selected matchup filters every block but the matchups. Family changes (merge, split, rename, main
/// version) go to the history and the page is shown again from the reloaded families. The Versions card's text comes
/// from <see cref="DeckVersionsText"/>.
/// </summary>
public sealed class DeckDetailsViewModel : Observable
{
    private readonly MainViewModel _owner;
    private DeckFamily? _family;
    private IReadOnlyList<DeckFamily> _families = [];
    private IReadOnlyList<MergeSuggestion> _suggestions = [];
    private IReadOnlySet<(string, string)> _dismissed = new HashSet<(string, string)>();
    private readonly HashSet<string> _openDiffs = new(StringComparer.Ordinal);
    private ConstructedMeta? _meta;
    private CardDb _cards = CardDb.Empty;
    private string? _matchup;
    private bool _sortByWinRate;
    private int _build;

    private string _name = string.Empty;
    private string _editName = string.Empty;
    private string _heroCardId = string.Empty;
    private string _subtitle = string.Empty;
    private string _archetypeLine = string.Empty;
    private string _filterText = string.Empty;
    private IReadOnlyList<StatTile> _tiles = [];
    private IReadOnlyList<IChartValue> _chart = [];
    private IReadOnlyList<int> _lengthWins = [];
    private IReadOnlyList<int> _lengthLosses = [];
    private IReadOnlyList<string> _lengthLabels = [];
    private IReadOnlyList<DeckMatchupRow> _matchups = [];
    private IReadOnlyList<DeckCardStatRow> _cardRows = [];
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
        SortMatchups = new RelayCommand(p =>
        {
            _sortByWinRate = p as string == "WinRate";
            Rebuild();
        });
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

    /// <summary>"Games" or "WinRate".</summary>
    public RelayCommand SortMatchups { get; }

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

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>"Blood DK · 54% in the meta", empty when the deck matches no archetype.</summary>
    public string ArchetypeLine { get => _archetypeLine; private set => Set(ref _archetypeLine, value); }

    /// <summary>"Only games vs Priest (6)" while a matchup is selected, else empty.</summary>
    public string FilterText { get => _filterText; private set => Set(ref _filterText, value); }

    public IReadOnlyList<StatTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }

    public IReadOnlyList<IChartValue> Chart { get => _chart; private set => Set(ref _chart, value); }

    public IReadOnlyList<int> LengthWins { get => _lengthWins; private set => Set(ref _lengthWins, value); }

    public IReadOnlyList<int> LengthLosses { get => _lengthLosses; private set => Set(ref _lengthLosses, value); }

    public IReadOnlyList<string> LengthLabels { get => _lengthLabels; private set => Set(ref _lengthLabels, value); }

    public IReadOnlyList<DeckMatchupRow> Matchups { get => _matchups; private set => Set(ref _matchups, value); }

    public bool SortedByWinRate => _sortByWinRate;

    public IReadOnlyList<DeckCardStatRow> CardRows { get => _cardRows; private set => Set(ref _cardRows, value); }

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

    private async void Rebuild()
    {
        if (_family is not { } family)
        {
            return;
        }

        var build = ++_build;
        var (families, suggestions, meta, cards, matchup, byWinRate) = (_families, _suggestions, _meta, _cards, _matchup, _sortByWinRate);
        try
        {
            var apply = await Task.Run(() => Build(family, families, suggestions, meta, cards, matchup, byWinRate)).ConfigureAwait(true);
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

    private Action Build(DeckFamily family, IReadOnlyList<DeckFamily> families, IReadOnlyList<MergeSuggestion> suggestions, ConstructedMeta? meta,
        CardDb cards, string? matchup, bool byWinRate)
    {
        // The family's games again with their card columns (lists read the history without them).
        var all = _owner.History.WithCards(family.Games);
        var games = DeckDetails.Filter(all, matchup, meta);
        var summary = DeckDetails.Summary(games);
        var deckRate = summary.Record.WinRate;
        var format = FormatName(family.Format);
        var metaFits = ArchetypeGuess.FitsFormat(meta, family.Format) ? meta : null;
        var archetype = ConstructedStats.ArchetypeOf(family.Main.DeckCode, family.PlayerClass, metaFits, cards);

        var subtitle = string.Join(" · ", new[]
        {
            OverlayViewModel.ClassName(family.PlayerClass),
            format,
            family.Versions.Count > 1 ? Loc.P("Deck.VersionCount", family.Versions.Count) : null,
            Loc.F("Deck.LastPlayed", MainViewModel.When(family.LastPlayed)),
        }.Where(s => !string.IsNullOrEmpty(s)));
        var archetypeLine = archetype is null ? string.Empty : Loc.F("Main.Meta.DeckInMeta", archetype.Name, Percent(archetype.WinRate));

        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Deck.Tile.WinRate"), Percent(deckRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile(Loc.T("Deck.Tile.Games"), summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture),
                all.Count != games.Count ? Loc.F("Deck.Tile.GamesOf", all.Count) : Loc.T("Deck.Tile.WithDeck")),
            new StatTile(Loc.T("Deck.Tile.AvgGame"), summary.AverageDuration is { } avg ? Loc.F("Deck.Minutes", avg.TotalMinutes) : "—",
                summary.AverageTurns is { } turns ? Loc.F("Deck.Tile.AvgTurns", turns) : "—"),
            new StatTile(Loc.T("Deck.Tile.OnPlay"), Percent(summary.OnPlay.WinRate), Loc.F("Deck.Tile.OnPlayDetail", summary.OnPlay.Wins, summary.OnPlay.Losses)),
            new StatTile(Loc.T("Deck.Tile.OnCoin"), Percent(summary.OnCoin.WinRate), Loc.F("Deck.Tile.OnCoinDetail", summary.OnCoin.Wins, summary.OnCoin.Losses)),
            archetype is not null && deckRate is { } rate
                ? new StatTile(Loc.T("Deck.Tile.VsMeta"), Signed((rate - archetype.WinRate) * 100), $"{archetype.Name} {Percent(archetype.WinRate)} · {metaFits!.Source}",
                    Math.Sign(Math.Round((rate - archetype.WinRate) * 100)))
                : new StatTile(Loc.T("Deck.Tile.VsMeta"), "—", metaFits is null ? Loc.T("Deck.Tile.MetaNotLoaded") : Loc.T("Deck.Tile.NoArchetype")),
        ];

        var chart = DeckDetails.WinRateTimeline(games)
            .Select(p => (IChartValue)new DeckGamePoint(
                p.WinRate * 100,
                Percent(p.WinRate),
                p.Game.Result,
                Opponent(p.Game, meta),
                GameDetail(p.Game),
                MainViewModel.When(p.Game.StartedAt),
                Loc.P("Deck.Chart.SoFar", p.SoFar.Games, p.SoFar.Wins, p.SoFar.Losses),
                p.Game.OpponentHeroCardId))
            .ToList();
        var lengths = ConstructedStats.ByLength(games);

        // Matchups count every game of the deck: they are the filter, not filtered.
        var classes = DeckDetails.ByOpponentClass(all);
        var archetypes = DeckDetails.ByOpponentArchetype(all, meta);
        IEnumerable<DeckMatchup> Sorted(IEnumerable<DeckMatchup> list) => byWinRate
            ? list.OrderByDescending(m => m.Record.WinRate ?? -1).ThenByDescending(m => m.Record.Games)
            : list.OrderByDescending(m => m.Record.Games).ThenByDescending(m => m.Record.WinRate ?? -1);
        var matchupRows = new List<DeckMatchupRow>();
        foreach (var opponent in Sorted(classes))
        {
            matchupRows.Add(MatchupRow(opponent, matchup));
            matchupRows.AddRange(Sorted(archetypes.Where(a => a.OpponentClass == opponent.OpponentClass)).Select(a => MatchupRow(a, matchup)));
        }

        var filterText = matchup is null
            ? string.Empty
            : Loc.F("Deck.Filter", matchupRows.FirstOrDefault(m => m.Key == matchup)?.Label ?? matchup, games.Count);

        // Personal mulligan and card numbers, next to the archetype's community mulligan when there is one.
        var metaCards = archetype?.Mulligan.ToDictionary(c => c.CardId, StringComparer.Ordinal) ?? new Dictionary<string, MetaMulliganCard>();
        var cardStats = DeckDetails.Cards(family, games, cards);
        var cardRows = cardStats.Select(c =>
            {
                var card = cards.Get(c.DbfId);
                var m = metaCards.GetValueOrDefault(c.CardId);
                return new DeckCardStatRow(
                    c.CardId,
                    card?.Name ?? c.CardId,
                    card?.Cost ?? 0,
                    c.Copies > 0 ? $"×{c.Copies}" : Loc.T("Deck.Mulligan.OtherVersion"),
                    c.Copies > 0,
                    c.Offered < DeckDetails.SmallSample,
                    c.WhenDrawn.Games < DeckDetails.SmallSample,
                    c.Offered.ToString(CultureInfo.CurrentCulture),
                    Percent(c.KeepRate),
                    Percent(c.WhenKept.WinRate),
                    Sign(c.WhenKept.WinRate, deckRate),
                    Percent(c.WhenReplaced.WinRate),
                    Sign(c.WhenReplaced.WinRate, deckRate),
                    m is null ? "—" : Percent(m.KeepRate),
                    m is null ? "—" : Percent(m.InHandWinRate),
                    c.WhenDrawn.Games.ToString(CultureInfo.CurrentCulture),
                    Percent(c.WhenDrawn.WinRate),
                    Sign(c.WhenDrawn.WinRate, deckRate),
                    c.WhenPlayed.Games.ToString(CultureInfo.CurrentCulture),
                    Percent(c.WhenPlayed.WinRate),
                    Sign(c.WhenPlayed.WinRate, deckRate),
                    c.AveragePlayTurn is { } t ? t.ToString("0.0", CultureInfo.CurrentCulture) : "—");
            })
            .ToList();
        var dataGames = DeckDetails.CardDataGames(games);
        var mulliganNote = (dataGames == 0
                ? Loc.T("Deck.Mulligan.NoData")
                : Loc.P("Deck.Mulligan.From", games.Count, dataGames, DeckDetails.SmallSample))
            + " " + (archetype is not null && metaCards.Count > 0
                ? Loc.F("Deck.Mulligan.Meta", archetype.Name, metaFits!.Source)
                : metaFits is null ? Loc.T("Deck.Mulligan.NeedsMeta") : Loc.T("Deck.Mulligan.NoMetaData"));
        var cardsNote = dataGames == 0
            ? Loc.T("Deck.Cards.NoData")
            : Loc.F("Deck.Cards.Note", Percent(deckRate));

        var main = family.Main;
        var versionRows = family.Versions.Select(v =>
            {
                var record = matchup is null ? v.Record : WinLoss.Of(games.Where(g => DeckFamilies.VersionKey(g) == v.Key));
                var deltas = v == main ? [] : DeckFamilies.Diff(main, v);
                return new DeckVersionRow(
                    v.Key,
                    v.Name,
                    v == main,
                    DeckVersionsText.VersionDetail(record.Games, MainViewModel.When(v.FirstPlayed), MainViewModel.When(v.LastPlayed)),
                    DeckVersionsText.Record(record),
                    Percent(record.WinRate),
                    record.WinRate ?? 0,
                    record.Games,
                    record.Games < DeckDetails.SmallSample,
                    Changes(deltas),
                    DiffRows(deltas, cards),
                    false,
                    family.Versions.Count > 1);
            })
            .ToList();

        var suggestionRows = suggestions
            .Where(s => s.First.Key == family.Key || s.Second.Key == family.Key)
            .Select(s =>
            {
                var other = s.First.Key == family.Key ? s.Second : s.First;
                var deltas = DeckFamilies.Diff(main, other.Main);
                return new DeckSuggestionRow(family.Key, other.Key, other.Name, DeckVersionsText.SuggestionDetail(s.Overlap, s.SameDeckId, s.SimilarNames, other.Record))
                {
                    Changes = Changes(deltas),
                    Diff = DiffRows(deltas, cards),
                };
            })
            .ToList();
        var dismissed = _owner.History.DismissedDeckPairs();

        var recent = games.AsEnumerable().Reverse().Take(12).Select(g => new DeckGameRow(
                g.Key,
                MainViewModel.When(g.StartedAt),
                g.Result,
                Opponent(g, meta),
                g.OpponentHeroCardId,
                GameDetail(g)))
            .ToList();

        return () =>
        {
            Name = family.Name;
            HeroCardId = MainViewModel.ClassHero(family.PlayerClass);
            Subtitle = subtitle;
            ArchetypeLine = archetypeLine;
            FilterText = filterText;
            Tiles = tiles;
            Chart = chart;
            LengthWins = lengths.Select(l => l.Record.Wins).ToList();
            LengthLosses = lengths.Select(l => l.Record.Losses).ToList();
            LengthLabels = lengths.Select(l => l.Label).ToList();
            Matchups = matchupRows;
            Raise(nameof(SortedByWinRate));
            CardRows = cardRows;
            MulliganNote = mulliganNote;
            CardsNote = cardsNote;
            ShowOpenDiffs(versionRows, suggestionRows);
            _dismissed = dismissed;
            RefreshCandidates();
            Games = recent;
        };
    }

    /// <summary>Card differences as tiles: added cards first, then the ones taken out.</summary>
    private static List<CardDiffRow> DiffRows(IReadOnlyList<CardDelta> deltas, CardDb cards) => deltas
        .Select(d => new CardDiffRow(
            cards.Get(d.DbfId)?.Id ?? string.Empty,
            cards.Get(d.DbfId)?.Name ?? d.DbfId.ToString(CultureInfo.InvariantCulture),
            $"{(d.Delta > 0 ? "+" : "−")}{Math.Abs(d.Delta)}",
            Math.Sign(d.Delta)))
        .ToList();

    /// <summary>"+2 / −2 cards", empty when the lists are the same (or unknown).</summary>
    private static string Changes(IReadOnlyList<CardDelta> deltas) => deltas.Count == 0
        ? string.Empty
        : DeckVersionsText.CardChanges(deltas.Where(d => d.Delta > 0).Sum(d => d.Delta), deltas.Where(d => d.Delta < 0).Sum(d => -d.Delta));

    private static DeckMatchupRow MatchupRow(DeckMatchup m, string? selected) => new(
        m.Key,
        m.Archetype ?? OverlayViewModel.ClassName(m.OpponentClass),
        m.Archetype is null ? MainViewModel.ClassHero(m.OpponentClass) : string.Empty,
        Loc.P("Deck.Games", m.Record.Games),
        $"{m.Record.Wins}–{m.Record.Losses}",
        Percent(m.Record.WinRate),
        m.Record.WinRate ?? 0,
        m.Archetype is not null,
        m.Key == selected,
        m.Record.Games < DeckDetails.SmallSample);

    private static string Opponent(GameRecord g, ConstructedMeta? meta)
    {
        var name = string.IsNullOrEmpty(g.OpponentClass) ? Loc.T("Deck.Game.UnknownOpponent") : OverlayViewModel.ClassName(g.OpponentClass);
        return Loc.F("Deck.Game.Vs", DeckDetails.OpponentArchetype(g, meta) is { } archetype ? archetype : name);
    }

    private static string GameDetail(GameRecord g) => string.Join(" · ", new[]
    {
        Loc.P("Deck.Game.Turns", g.Turns),
        g.Duration > TimeSpan.Zero ? Loc.F("Deck.Minutes", g.Duration.TotalMinutes) : null,
        g.WentFirst switch { true => Loc.T("Deck.Game.WentFirst"), false => Loc.T("Deck.Game.OnCoin"), _ => null },
        Core.Memory.RankInfo.Localize(g.Rank),
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>A history result ("Win", "Loss"...) as History shows it; the colour keeps the stored value.</summary>
    internal static string ResultText(string result) => result switch
    {
        "Win" => Loc.T("Main.Result.Win"),
        "Loss" => Loc.T("Main.Result.Loss"),
        "Tie" => Loc.T("Main.Result.Tie"),
        GameRecord.AbandonedResult => Loc.T("Main.Result.Abandoned"),
        "Unknown" => Loc.T("Main.Result.Unknown"),
        _ => result,
    };

    /// <summary>+1 when a card's win rate is 5 points or more above the deck's, -1 when as far below.</summary>
    private static int Sign(double? rate, double? deck) =>
        rate is { } r && deck is { } d ? (r - d >= 0.05 ? 1 : d - r >= 0.05 ? -1 : 0) : 0;

    private static string Percent(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Signed(double points)
    {
        var rounded = Math.Round(points);
        return rounded > 0 ? $"+{rounded:0}%" : rounded < 0 ? $"−{-rounded:0}%" : "±0%";
    }
}
