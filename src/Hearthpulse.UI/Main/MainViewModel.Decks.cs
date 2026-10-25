using System.Globalization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Decks;
using Hearthpulse.UI.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.UI.Main;

/// <summary>Your decks as families (merged versions), merge suggestions and the deck page.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The last merge, until another family change or a history change (a saved game, deck codes filled in): the state
    /// to put back, the deck merged in (for the "Merged ..." line), the deck page shown before it (null when merged from
    /// "Your decks"), the key the kept deck had before and the family it made.
    /// </summary>
    private sealed record MergeUndo(DeckFamilySnapshot Before, string MergedName, string? PageBefore, string KeptBefore, string Result);

    private MergeUndo? _mergeUndo;

    /// <summary>
    /// True from a family change (or a history reload) until the deck list is rebuilt: the families shown are stale, so
    /// merge, "not the same", undo and the version actions wait (a double click on Merge would act on the old ones).
    /// </summary>
    private bool _decksStale;
    private IReadOnlyList<DeckFamily> _deckFamilies = [];
    private IReadOnlyList<MergeSuggestion> _mergeSuggestions = [];
    private IReadOnlyList<DeckSuggestionRow> _deckSuggestions = [];
    private DeckDetailsViewModel? _deckPage;
    private bool _deckOpen;
    private string? _openDeckKey;
    private int _decksBuild;
    private bool _familiesWatching;
    private RelayCommand? _openDeck;
    private RelayCommand? _mergeSuggested;
    private RelayCommand? _dismissSuggested;

    /// <summary>The player asked to see a game in History (its game key).</summary>
    public event Action<string>? HistoryRequested;

    public DeckDetailsViewModel DeckPage => _deckPage ??= new DeckDetailsViewModel(this);

    /// <summary>True while the deck page replaces the Constructed page.</summary>
    public bool DeckOpen { get => _deckOpen; private set => Set(ref _deckOpen, value); }

    /// <summary>Decks that look like versions of one, for the Decks card.</summary>
    public IReadOnlyList<DeckSuggestionRow> DeckSuggestions { get => _deckSuggestions; private set => Set(ref _deckSuggestions, value); }

    /// <summary>Opens the deck page of a <see cref="DeckRow"/> (or a family key).</summary>
    public RelayCommand OpenDeck => _openDeck ??= new RelayCommand(p =>
    {
        var key = p is DeckRow row ? row.Key : p as string;
        if (key is not null)
        {
            OpenDeckPage(key);
        }
    });

    public RelayCommand MergeSuggested => _mergeSuggested ??= new RelayCommand(p => MergeDecks(p as DeckSuggestionRow));

    public RelayCommand DismissSuggested => _dismissSuggested ??= new RelayCommand(p => DismissDecks(p as DeckSuggestionRow));

    /// <summary>Opens a deck page by family key (also used by the UI render check). False when there is no such deck.</summary>
    public bool OpenDeckPage(string key)
    {
        if (_deckFamilies.FirstOrDefault(f => f.Key == key) is not { } family)
        {
            return false;
        }

        _openDeckKey = key;

        // A deck opens on its format's page (a Wild deck on Wild); the window moves the nav along.
        ShowConstructedFormat(ConstructedPage.FormatOf(ConstructedPage.PageOf(family.Format)));
        DeckPage.Show(family, _deckFamilies, _mergeSuggestions, ConstructedMetaFor(family.Format), _cards);
        DeckOpen = true;
        return true;
    }

    /// <summary>The families as last built, most recently played first.</summary>
    public IReadOnlyList<DeckFamily> DeckFamiliesShown => _deckFamilies;

    internal void CloseDeck()
    {
        DeckOpen = false;
        _openDeckKey = null;
    }

    /// <summary>The history, for the deck page's card columns (read on its build thread).</summary>
    internal HistoryStore History => _history;

    internal void ShowHistoryGame(string key) => HistoryRequested?.Invoke(key);

    /// <summary>
    /// The History row of a game for the deck page's link. A game older than the rows the page lists is added on top,
    /// so the link always lands on it.
    /// </summary>
    public GameRow? HistoryRowFor(string key)
    {
        if (HistoryRows.FirstOrDefault(r => r.Key == key) is { } shown)
        {
            return shown;
        }

        if (_history.Game(key) is not { } game)
        {
            return null;
        }

        var row = Row(game);
        HistoryRows = [row, .. HistoryRows];
        return row;
    }

    internal void MergeDecks(DeckSuggestionRow? pair)
    {
        if (_decksStale)
        {
            return;
        }

        var first = _deckFamilies.FirstOrDefault(f => f.Key == pair?.FirstKey);
        var second = _deckFamilies.FirstOrDefault(f => f.Key == pair?.SecondKey);
        if (first is null || second is null)
        {
            return;
        }

        // The deck the player is on (or the first of the pair) keeps its name and main version.
        var keep = _openDeckKey == second.Key ? second : first;
        var other = keep == first ? second : first;
        var keys = keep.Versions.Concat(other.Versions).Select(v => v.Key).ToList();
        var previous = _mergeUndo;
        DeckFamilySnapshot? before = null;
        var pageBefore = _openDeckKey;
        var id = FamilyChange(() =>
        {
            (var merged, before) = _history.MergeDecksUndoable(keys, keep.Main.Key, into: keep.Id);
            return merged;
        });
        if (id is not { } family)
        {
            return;
        }

        var result = DeckFamilies.FamilyPrefix + family.ToString(CultureInfo.InvariantCulture);

        // Nothing merged (already one deck): the undo of the merge that did it stays.
        _mergeUndo = before is null ? previous : new MergeUndo(before, other.Name, pageBefore, keep.Key, result);
        if (DeckOpen)
        {
            _openDeckKey = result;
        }
    }

    /// <summary>"Merged “name” into this deck" while the last merge made this family and can still be undone, else null.</summary>
    internal string? MergeUndoText(string familyKey) =>
        _mergeUndo is { } undo && undo.Result == familyKey ? DeckVersionsText.Merged(undo.MergedName) : null;

    /// <summary>Puts the families back as they were before the last merge; the deck page goes back to the deck it showed.</summary>
    internal void UndoMerge()
    {
        if (_decksStale || _mergeUndo is not { } undo)
        {
            return;
        }

        // The page goes back to the deck it showed; after a merge from "Your decks", to the deck that was kept.
        if (DeckOpen && _openDeckKey == undo.Result)
        {
            _openDeckKey = undo.PageBefore ?? undo.KeptBefore;
        }

        FamilyChange(() =>
        {
            _history.RestoreDeckFamilies(undo.Before);
            return 0;
        });
    }

    internal void DismissDecks(DeckSuggestionRow? pair)
    {
        if (_decksStale)
        {
            return;
        }

        var first = _deckFamilies.FirstOrDefault(f => f.Key == pair?.FirstKey);
        var second = _deckFamilies.FirstOrDefault(f => f.Key == pair?.SecondKey);
        if (first is not null && second is not null)
        {
            // Every version of one against every version of the other, so a later split does not bring the pair back.
            FamilyChange(() =>
            {
                _history.DismissDecks(first.Versions.Select(v => v.Key).ToList(), second.Versions.Select(v => v.Key).ToList());
                return 0;
            });
        }
    }

    internal void RenameDeck(DeckFamily? family, string? name)
    {
        if (family is null)
        {
            return;
        }

        if (family.Id is { } id)
        {
            FamilyChange(() =>
            {
                _history.RenameDeckFamily(id, name);
                return id;
            });
        }
        else if (!string.IsNullOrWhiteSpace(name) && name.Trim() != family.Name)
        {
            // A lone version gets a family of one to hold its name.
            var created = FamilyChange(() => _history.MergeDecks([family.Main.Key], name, family.Main.Key));
            if (created is { } fresh)
            {
                _openDeckKey = DeckFamilies.FamilyPrefix + fresh.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    internal void SetMainVersion(DeckFamily? family, string? versionKey)
    {
        if (!_decksStale && family?.Id is { } id && versionKey is not null)
        {
            FamilyChange(() =>
            {
                _history.SetDeckFamilyMain(id, versionKey);
                return id;
            });
        }
    }

    internal void SplitVersion(DeckFamily? family, string? versionKey)
    {
        if (_decksStale || family is null || versionKey is null || family.Versions.Count < 2)
        {
            return;
        }

        // The page stays on the family, or on the one version left when the family is dissolved.
        var rest = family.Versions.Where(v => v.Key != versionKey).ToList();
        FamilyChange(() =>
        {
            _history.SplitDeck(versionKey);
            return 0;
        });
        if (rest.Count == 1 && _openDeckKey == family.Key)
        {
            _openDeckKey = rest[0].Key;
        }
    }

    /// <summary>
    /// Runs a family change on the history; the change event rebuilds the decks. Null when it failed. Any change ends the
    /// undo of the last merge (a merge sets a new one after this).
    /// </summary>
    private long? FamilyChange(Func<long> change)
    {
        _mergeUndo = null;
        try
        {
            return change();
        }
        catch (Exception ex)
        {
            Diag.Error("Deck family change failed", ex);
            return null;
        }
    }

    /// <summary>Rebuilds the deck list, suggestions, overall matchups and the open deck page from the loaded games.</summary>
    private async void RebuildDecks()
    {
        if (!_familiesWatching)
        {
            _familiesWatching = true;
            _history.DeckFamiliesChanged += RebuildDecks;

            // A saved game or filled-in deck codes can move versions into a family: an older snapshot would lose them.
            _history.Changed += () => _mergeUndo = null;
        }

        var build = ++_decksBuild;
        _decksStale = true;
        var (games, cards, format) = (_allGames, _cards, _ctFormat);
        try
        {
            var data = await Task.Run(() =>
            {
                var versions = DeckFamilies.Versions(games);
                var families = DeckFamilies.Build(versions, _history.DeckFamilies());
                var suggestions = DeckFamilies.Suggest(families, _history.DismissedDeckPairs());

                // The Standard and Wild pages list only their format's decks; the deck page still sees every family.
                var shown = ConstructedPage.Decks(families, format);
                return (Families: families, Suggestions: suggestions, Shown: shown, ShownSuggestions: ConstructedPage.Suggestions(suggestions, format),
                    Matchups: DeckDetails.ByOpponentClass(shown.SelectMany(f => f.Games).ToList()));
            }).ConfigureAwait(true);
            if (build != _decksBuild)
            {
                return;
            }

            _deckFamilies = data.Families;
            _mergeSuggestions = data.Suggestions;
            _decksStale = false;
            Decks = data.Shown.Select(f => new DeckRow(
                    f.Key,
                    f.Name,
                    OverlayViewModel.ClassName(f.PlayerClass),
                    f.Record.Games,
                    $"{f.Record.Wins}–{f.Record.Losses}",
                    (f.Record.WinRate ?? 0).ToString("P0", CultureInfo.CurrentCulture),
                    f.Record.WinRate ?? 0,
                    When(f.LastPlayed),
                    f.Main.DeckCode,
                    ConstructedStats.ArchetypeOf(f.Main.DeckCode, f.PlayerClass, ConstructedMetaFor(f.Format), _cards) is { } archetype
                        ? Loc.F("Main.Meta.DeckInMeta", archetype.Name, archetype.WinRate.ToString("P0", CultureInfo.CurrentCulture))
                        : string.Empty,
                    f.Versions.Count > 1 ? Loc.P("Deck.VersionCount", f.Versions.Count) : string.Empty))
                .ToList();
            DeckSuggestions = data.ShownSuggestions.Select(s => new DeckSuggestionRow(
                    s.First.Key,
                    s.Second.Key,
                    $"{s.First.Name} + {s.Second.Name}",
                    DeckVersionsText.SuggestionDetail(s.Overlap, s.SameDeckId, s.SimilarNames)))
                .ToList();
            Matchups = data.Matchups.Select(m => new MatchupRow(
                    OverlayViewModel.ClassName(m.OpponentClass),
                    m.Record.Games,
                    $"{m.Record.Wins}–{m.Record.Losses}",
                    (m.Record.WinRate ?? 0).ToString("P0", CultureInfo.CurrentCulture),
                    m.Record.WinRate ?? 0))
                .ToList();

            if (DeckOpen && _openDeckKey is { } key && !OpenDeckPage(key))
            {
                CloseDeck();
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Deck list build failed", ex);
            if (build == _decksBuild)
            {
                _decksStale = false;
            }
        }
    }
}
