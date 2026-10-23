using System.Globalization;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.UI.Main.Decks;

/// <summary>A game on the deck's win-rate line, with what its hover card shows.</summary>
public sealed record DeckGamePoint(
    double ChartValue,
    string ChartLabel,
    string Result,
    string Title,
    string Detail,
    string When,
    string SoFar,
    string OpponentHeroCardId) : IChartValue;

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

/// <summary>A card whose copies differ from the main version: "+2 Card" or "−1 Card".</summary>
public sealed record CardDiffRow(string CardId, string Text, int Sign);

/// <summary>A version of the deck with its own results and its cards against the main version.</summary>
public sealed record DeckVersionRow(
    string Key,
    string Name,
    bool IsMain,
    string Detail,
    string Record,
    string WinRate,
    double WinFraction,
    IReadOnlyList<CardDiffRow> Diff,
    bool CanManage);

/// <summary>Another deck that looks like a version of this one.</summary>
public sealed record DeckSuggestionRow(string FirstKey, string SecondKey, string Title, string Detail);

/// <summary>A recent game of the deck.</summary>
public sealed record DeckGameRow(string Key, string When, string Result, string Opponent, string OpponentHeroCardId, string Detail);

/// <summary>Another deck of the class, to merge with by hand.</summary>
public sealed record DeckChoice(string Key, string Label);

/// <summary>
/// The deck page: one deck family's results, matchups, personal mulligan, card performance, versions and recent
/// games. A selected matchup filters every block but the matchups. Family changes (merge, split, rename, main
/// version) go to the history and the page is shown again from the reloaded families.
/// </summary>
public sealed class DeckDetailsViewModel : Observable
{
    private readonly MainViewModel _owner;
    private DeckFamily? _family;
    private IReadOnlyList<DeckFamily> _families = [];
    private IReadOnlyList<MergeSuggestion> _suggestions = [];
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
    private IReadOnlyList<DeckChoice> _mergeChoices = [];
    private DeckChoice? _mergeChoice;
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
        Rename = new RelayCommand(() => _owner.RenameDeck(_family, EditName));
        SetMain = new RelayCommand(p => _owner.SetMainVersion(_family, p as string));
        Split = new RelayCommand(p => _owner.SplitVersion(_family, p as string));
        Merge = new RelayCommand(p => _owner.MergeDecks(p as DeckSuggestionRow));
        Dismiss = new RelayCommand(p => _owner.DismissDecks(p as DeckSuggestionRow));
        MergeChosen = new RelayCommand(() =>
        {
            if (_family is not null && MergeChoice is { } choice)
            {
                _owner.MergeDecks(new DeckSuggestionRow(_family.Key, choice.Key, string.Empty, string.Empty));
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

    public RelayCommand Rename { get; }

    public RelayCommand SetMain { get; }

    public RelayCommand Split { get; }

    public RelayCommand Merge { get; }

    public RelayCommand Dismiss { get; }

    public RelayCommand MergeChosen { get; }

    public RelayCommand OpenGame { get; }

    /// <summary>The family key of the deck shown.</summary>
    public string? Key => _family?.Key;

    public string Name { get => _name; private set => Set(ref _name, value); }

    /// <summary>The name box; <see cref="Rename"/> saves it (blank goes back to the main version's name).</summary>
    public string EditName { get => _editName; set => Set(ref _editName, value); }

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

    public IReadOnlyList<DeckChoice> MergeChoices { get => _mergeChoices; private set => Set(ref _mergeChoices, value); }

    public DeckChoice? MergeChoice { get => _mergeChoice; set => Set(ref _mergeChoice, value); }

    public IReadOnlyList<DeckGameRow> Games { get => _games; private set => Set(ref _games, value); }

    /// <summary>Shows a deck. The matchup filter is kept when the same deck is shown again (after a family change).</summary>
    public void Show(DeckFamily family, IReadOnlyList<DeckFamily> families, IReadOnlyList<MergeSuggestion> suggestions, ConstructedMeta? meta, CardDb cards)
    {
        if (_family?.Key != family.Key)
        {
            _matchup = null;
            EditName = family.Name;
        }

        _family = family;
        _families = families;
        _suggestions = suggestions;
        _meta = meta;
        _cards = cards;
        Raise(nameof(Key));
        Rebuild();
    }

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
        var games = DeckDetails.Filter(family.Games, matchup, meta);
        var summary = DeckDetails.Summary(games);
        var deckRate = summary.Record.WinRate;
        var format = family.Format == "FT_WILD" ? "Wild" : family.Format == "FT_STANDARD" ? "Standard" : family.Format.Replace("FT_", string.Empty, StringComparison.Ordinal);
        var metaFits = ArchetypeGuess.FitsFormat(meta, family.Format) ? meta : null;
        var archetype = ConstructedStats.ArchetypeOf(family.Main.DeckCode, family.PlayerClass, metaFits, cards);

        var subtitle = string.Join(" · ", new[]
        {
            OverlayViewModel.ClassName(family.PlayerClass),
            format,
            family.Versions.Count > 1 ? $"{family.Versions.Count} versions" : null,
            $"last played {MainViewModel.When(family.LastPlayed)}",
        }.Where(s => !string.IsNullOrEmpty(s)));
        var archetypeLine = archetype is null ? string.Empty : $"{archetype.Name} · {Percent(archetype.WinRate)} in the meta";

        IReadOnlyList<StatTile> tiles =
        [
            new StatTile("Win rate", Percent(deckRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile("Games", summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture),
                family.Games.Count != games.Count ? $"of {family.Games.Count:N0} with this deck" : "with this deck"),
            new StatTile("Avg. game", summary.AverageDuration is { } avg ? $"{avg.TotalMinutes:0} min" : "—",
                summary.AverageTurns is { } turns ? $"{turns:0.0} turns" : "—"),
            new StatTile("On the play", Percent(summary.OnPlay.WinRate), $"{summary.OnPlay.Wins}–{summary.OnPlay.Losses} going first"),
            new StatTile("On the coin", Percent(summary.OnCoin.WinRate), $"{summary.OnCoin.Wins}–{summary.OnCoin.Losses} going second"),
            archetype is not null && deckRate is { } rate
                ? new StatTile("vs Meta", Signed((rate - archetype.WinRate) * 100), $"{archetype.Name} {Percent(archetype.WinRate)} · {metaFits!.Source}",
                    Math.Sign(Math.Round((rate - archetype.WinRate) * 100)))
                : new StatTile("vs Meta", "—", metaFits is null ? "community meta not loaded for this format" : "no meta archetype matches this list"),
        ];

        var chart = DeckDetails.WinRateTimeline(games)
            .Select(p => (IChartValue)new DeckGamePoint(
                p.WinRate * 100,
                Percent(p.WinRate),
                p.Game.Result,
                Opponent(p.Game, meta),
                GameDetail(p.Game),
                MainViewModel.When(p.Game.StartedAt),
                $"{p.SoFar.Wins}–{p.SoFar.Losses} after {p.SoFar.Games} games",
                p.Game.OpponentHeroCardId))
            .ToList();
        var lengths = ConstructedStats.ByLength(games);

        // Matchups count every game of the deck: they are the filter, not filtered.
        var classes = DeckDetails.ByOpponentClass(family.Games);
        var archetypes = DeckDetails.ByOpponentArchetype(family.Games, meta);
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
            : $"Only games vs {matchupRows.FirstOrDefault(m => m.Key == matchup)?.Label ?? matchup} ({games.Count})";

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
                    c.Copies > 0 ? $"×{c.Copies}" : "other version",
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
                ? "No game of this deck saved its mulligan yet: games from now on fill this in."
                : $"From {dataGames:N0} of {games.Count:N0} games (older games did not save cards). Under {DeckDetails.SmallSample} offers a card is greyed out: too few games to tell.")
            + (archetype is not null && metaCards.Count > 0
                ? $" Meta: {archetype.Name}, {metaFits!.Source}."
                : metaFits is null ? " Meta columns need the community meta of this format." : " The meta has no mulligan data for this list.");
        var cardsNote = dataGames == 0
            ? "Cards drawn and played are saved from now on."
            : $"Per game: drawn = in hand at any point (a kept opening card counts), played = played from hand. Win rates are coloured against the deck's {Percent(deckRate)}.";

        var main = family.Main;
        var versionRows = family.Versions.Select(v =>
            {
                var record = matchup is null ? v.Record : WinLoss.Of(games.Where(g => DeckFamilies.VersionKey(g) == v.Key));
                return new DeckVersionRow(
                    v.Key,
                    v.Name,
                    v == main,
                    $"{(record.Games == 1 ? "1 game" : $"{record.Games:N0} games")} · {MainViewModel.When(v.FirstPlayed)} – {MainViewModel.When(v.LastPlayed)}",
                    $"{record.Wins}–{record.Losses}",
                    Percent(record.WinRate),
                    record.WinRate ?? 0,
                    v == main ? [] : DeckFamilies.Diff(main, v).Select(d => new CardDiffRow(
                            cards.Get(d.DbfId)?.Id ?? string.Empty,
                            $"{(d.Delta > 0 ? "+" : "−")}{Math.Abs(d.Delta)} {cards.Get(d.DbfId)?.Name ?? d.DbfId.ToString(CultureInfo.InvariantCulture)}",
                            Math.Sign(d.Delta)))
                        .ToList(),
                    family.Versions.Count > 1);
            })
            .ToList();

        var suggestionRows = suggestions
            .Where(s => s.First.Key == family.Key || s.Second.Key == family.Key)
            .Select(s =>
            {
                var other = s.First.Key == family.Key ? s.Second : s.First;
                var hints = string.Join(", ", new[] { s.SameDeckId ? "same deck slot" : null, s.SimilarNames ? "alike names" : null }.Where(h => h is not null));
                return new DeckSuggestionRow(family.Key, other.Key, other.Name,
                    $"{Percent(s.Overlap)} same cards{(hints.Length > 0 ? " · " + hints : string.Empty)} · {other.Record.Wins}–{other.Record.Losses}");
            })
            .ToList();
        var choices = families
            .Where(f => f.Key != family.Key && f.PlayerClass == family.PlayerClass)
            .Select(f => new DeckChoice(f.Key, $"{f.Name} · {f.Record.Wins}–{f.Record.Losses}"))
            .ToList();

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
            Versions = versionRows;
            Suggestions = suggestionRows;
            MergeChoices = choices;
            MergeChoice = choices.FirstOrDefault(c => c.Key == MergeChoice?.Key);
            Games = recent;
        };
    }

    private static DeckMatchupRow MatchupRow(DeckMatchup m, string? selected) => new(
        m.Key,
        m.Archetype ?? OverlayViewModel.ClassName(m.OpponentClass),
        m.Archetype is null ? MainViewModel.ClassHero(m.OpponentClass) : string.Empty,
        m.Record.Games == 1 ? "1 game" : $"{m.Record.Games:N0} games",
        $"{m.Record.Wins}–{m.Record.Losses}",
        Percent(m.Record.WinRate),
        m.Record.WinRate ?? 0,
        m.Archetype is not null,
        m.Key == selected,
        m.Record.Games < DeckDetails.SmallSample);

    private static string Opponent(GameRecord g, ConstructedMeta? meta)
    {
        var name = string.IsNullOrEmpty(g.OpponentClass) ? "Unknown opponent" : OverlayViewModel.ClassName(g.OpponentClass);
        return DeckDetails.OpponentArchetype(g, meta) is { } archetype ? $"vs {archetype}" : $"vs {name}";
    }

    private static string GameDetail(GameRecord g) => string.Join(" · ", new[]
    {
        $"{g.Turns} turns",
        g.Duration > TimeSpan.Zero ? $"{g.Duration.TotalMinutes:0} min" : null,
        g.WentFirst switch { true => "went first", false => "on the coin", _ => null },
        g.Rank,
    }.Where(s => !string.IsNullOrEmpty(s)));

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
