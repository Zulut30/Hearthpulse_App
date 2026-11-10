#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Runtime.CompilerServices;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>A chip of the turn strip; <see cref="Selected"/> holds the step on screen.</summary>
public sealed record MatchTurnRow(int FirstFrame, string Label, string Tooltip, bool Local, bool Mulligan, bool Selected) : IImmutableView;

/// <summary>
/// The match page (opened from History): header, the step-by-step replay seen from either side (Constructed) or the
/// Battlegrounds table, both decks, the Battlegrounds rounds and lobby. Everything shown comes from
/// <see cref="MatchPage"/> in Core; this only keeps the position (step, side, tab) and the player bar
/// (<c>MatchPlayer.cs</c>).
/// </summary>
public sealed partial class MatchViewModel : Observable
{
    private readonly MainViewModel _owner;
    private MatchPage? _page;
    private int _frame;
    private bool _bottomLocal = true;
    private string _tab = "Replay";
    private bool _loading;
    private MatchHeader? _header;
    private string _missing = string.Empty;
    private MatchStepView? _step;
    private readonly SyncList<MatchTurnRow> _turns = new();

    // The events of the step's turn (Constructed) or stretch (Battlegrounds): null until asked for (no view shows the
    // Constructed list: the feed took its place) or set by the Battlegrounds step.
    private IReadOnlyList<MatchActionRow>? _actions = [];
    private int _actionCurrentIndex = -1;

    // The page and its turn the turn list was built for (Refresh rebuilds it only when either changes).
    private MatchPage? _turnsPage;
    private MatchTurnChip? _turnsCurrent;

    // The game's event feed (plan R2): its rows built once per game and kept, each step only moves the marks (Current)
    // of the rows that change; the last step of each header's turn; the step's row in the list (-1: none).
    private IReadOnlyList<MatchFeedItem> _feedRows = [];
    private int[] _feedTurnEnds = [];
    private int _feedCurrentIndex = -1;
    private IReadOnlyList<MatchDeckRow> _localDeck = [];
    private IReadOnlyList<MatchDeckRow> _opponentCards = [];
    private IReadOnlyList<MatchDeckRow> _opponentPredicted = [];
    private string _opponentPredictedNote = string.Empty;
    private string _tableNote = string.Empty;
    private string _opponentCardsNote = string.Empty;
    private string _opponentCardsEmpty = string.Empty;
    private IReadOnlyList<MatchRoundView> _rounds = [];
    private IReadOnlyList<MatchLobbyRow> _lobby = [];
    private string _localDeckNote = string.Empty;
    private string _archetypeLine = string.Empty;
    private MatchAnalysis? _analysis;

    /// <param name="owner">The main window's model (Back closes the page there).</param>
    /// <param name="delay">The wait between played steps; tests pass their own.</param>
    public MatchViewModel(MainViewModel owner, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
        _owner = owner;
        Back = new RelayCommand(owner.CloseMatch);
        Previous = new RelayCommand(() => Frame--);
        Next = new RelayCommand(() => Frame++);
        First = new RelayCommand(() => Frame = 0);
        Last = new RelayCommand(() => Frame = FrameMax);
        PreviousTurn = new RelayCommand(() => GoTurn(-1));
        NextTurn = new RelayCommand(() => GoTurn(+1));
        PreviousEvent = new RelayCommand(() => GoEvent(-1));
        NextEvent = new RelayCommand(() => GoEvent(+1));
        TogglePlay = new RelayCommand(PlayOrPause);
        Faster = new RelayCommand(() => ChangeSpeed(+1, wrap: false));
        Slower = new RelayCommand(() => ChangeSpeed(-1, wrap: false));
        CycleSpeed = new RelayCommand(() => ChangeSpeed(+1, wrap: true));
        GoFrame = new RelayCommand(p => Frame = p switch
        {
            MatchTurnRow turn => turn.FirstFrame,
            MatchActionRow action => action.Frame,
            MatchFeedRow row => row.Frame,
            MatchFeedItem row => row.Frame,
            MatchActionItem action => action.Frame,
            int frame => frame,
            _ => Frame,
        });
        SetTab = new RelayCommand(p => Tab = p as string ?? "Replay");
    }

    public RelayCommand Back { get; }

    public RelayCommand Previous { get; }

    public RelayCommand Next { get; }

    public RelayCommand First { get; }

    public RelayCommand Last { get; }

    public RelayCommand PreviousTurn { get; }

    public RelayCommand NextTurn { get; }

    /// <summary>Goes to a turn chip, an action row, a feed row (a header: its turn's first step) or a step number (the timeline passes one).</summary>
    public RelayCommand GoFrame { get; }

    /// <summary>"Replay", "Decks", "Analysis" (Constructed); "Replay" (format 4), "Rounds", "Lobby" (Battlegrounds).</summary>
    public RelayCommand SetTab { get; }

    /// <summary>True while the game's replay is being read.</summary>
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public MatchHeader? Header { get => _header; private set => Set(ref _header, value); }

    /// <summary>Why there is nothing to replay, or empty.</summary>
    public string Missing { get => _missing; private set => Set(ref _missing, value); }

    public bool IsConstructed => _page is { Replay: not null };

    public bool IsBattlegrounds => _page is { Battlegrounds: not null } || HasBattlegroundsSteps;

    /// <summary>The game's file holds the Battlegrounds step-by-step replay (format 4).</summary>
    public bool HasBattlegroundsSteps => _page is { HasBattlegroundsSteps: true };

    /// <summary>The Replay tab exists: a Constructed replay or a Battlegrounds step-by-step one.</summary>
    public bool HasPlayer => IsConstructed || HasBattlegroundsSteps;

    public bool ShowOpponent => _page is { IsBattlegrounds: false };

    public string Tab
    {
        get => _tab;
        private set
        {
            if (Set(ref _tab, value))
            {
                Pause();
                ClearHeaderStatus();
                RaiseTabs();
            }
        }
    }

    // The tab and side radios bind two-way: a radio being unchecked by its group sets false, which is ignored.
    /// <summary>The Replay tab is open: the player bar and the table of either mode.</summary>
    public bool ShowReplay { get => HasPlayer && Tab == "Replay"; set => Pick(value, "Replay"); }

    public bool ShowConstructedTable => IsConstructed && Tab == "Replay";

    public bool ShowBattlegroundsTable => HasBattlegroundsSteps && Tab == "Replay";

    public bool ShowDecks { get => IsConstructed && Tab == "Decks"; set => Pick(value, "Decks"); }

    public bool ShowAnalysis { get => IsConstructed && Tab == "Analysis"; set => Pick(value, "Analysis"); }

    public bool ShowRounds { get => IsBattlegrounds && Tab != "Lobby" && !ShowBattlegroundsTable; set => Pick(value, "Rounds"); }

    public bool ShowLobby { get => IsBattlegrounds && Tab == "Lobby"; set => Pick(value, "Lobby"); }

    /// <summary>The player's side is at the bottom of the table.</summary>
    public bool BottomLocal
    {
        get => _bottomLocal;
        set
        {
            if (value && Set(ref _bottomLocal, true))
            {
                Raise(nameof(BottomOpponent));
                Refresh();
            }
        }
    }

    /// <summary>The opponent's side is at the bottom of the table.</summary>
    public bool BottomOpponent
    {
        get => !_bottomLocal;
        set
        {
            if (value && Set(ref _bottomLocal, false, nameof(BottomLocal)))
            {
                Raise();
                Refresh();
            }
        }
    }

    /// <summary>The step on screen (also bound two-way by the step slider).</summary>
    public int Frame
    {
        get => _frame;
        set
        {
            var clamped = Math.Clamp(value, 0, FrameMax);
            if (Set(ref _frame, clamped))
            {
                Refresh();
            }
        }
    }

    public int FrameMax => Math.Max(0, (_page?.FrameCount ?? 1) - 1);

    /// <summary>The first step of an action kind (the mulligan, a Discover), or -1; render scenes go to it.</summary>
    public int FrameOf(ReplayActionKind kind) => _page?.Replay?.Frames.FirstOrDefault(f => f.Action.Kind == kind)?.Index ?? -1;

    /// <summary>The first Constructed step whose table (the player's side at the bottom) matches, or -1; render scenes go to it.</summary>
    public int FrameWhere(Func<MatchStepView, bool> match)
    {
        for (var frame = 0; _page is { Replay: not null } page && frame < page.FrameCount; frame++)
        {
            if (page.Step(frame, ReplayPlayer.Local) is { } step && match(step))
            {
                return frame;
            }
        }

        return -1;
    }

    /// <summary>The Constructed step on screen, as Core built it. The view binds <see cref="Table"/>, which shows it in place.</summary>
    public MatchStepView? Step
    {
        get => _step;
        private set
        {
            _step = value;
            if (Table.Update(value))
            {
                Raise();
            }
        }
    }

    /// <summary>
    /// The Constructed table the view binds: the same object, sides and cards for the whole page, changed in place by
    /// each step (<see cref="MatchTable"/>).
    /// </summary>
    public MatchTable Table { get; } = new();

    /// <summary>"Step 34 of 210 · turn 7"; before the first turn "Step 2 of 210".</summary>
    public string Position => _page is null ? string.Empty
        : TurnAt(Frame) is > 0 and var turn ? Loc.F("Match.Player.Position", Frame + 1, FrameMax + 1, turn) : Loc.F("Match.Position", Frame + 1, FrameMax + 1);

    /// <summary>The events of the step's turn (Constructed) or of its tavern or combat (Battlegrounds), the latest one marked.</summary>
    public IReadOnlyList<MatchActionRow> Actions
    {
        get => _actions ??= _page?.Actions(_frame) ?? [];
        private set
        {
            if (_actions is not null && _actions.SequenceEqual(value))
            {
                return;
            }

            _actions = value;
            MatchItems.Sync(ActionRows, value);
            var current = -1;
            for (var i = 0; i < value.Count && current < 0; i++)
            {
                current = value[i].Current ? i : -1;
            }

            Raise();
            // After the rows: the view scrolls to the index of a row that is there.
            _actionCurrentIndex = current;
            Raise(nameof(ActionCurrentIndex));
        }
    }

    /// <summary>The Battlegrounds list of events as the view binds it: the rows stay, a step moves the mark.</summary>
    public System.Collections.ObjectModel.ObservableCollection<MatchActionItem> ActionRows { get; } = [];

    /// <summary>The marked row of <see cref="ActionRows"/> (the view keeps it in sight), or -1.</summary>
    public int ActionCurrentIndex => _actionCurrentIndex;

    public IReadOnlyList<MatchTurnRow> Turns { get => _turns; private set => Keep(_turns, value); }

    /// <summary>
    /// The Constructed game's event feed (plan R2): every action under its turn's header, the step on screen and its
    /// turn marked (empty for Battlegrounds). The view binds <see cref="FeedRows"/>.
    /// </summary>
    public IReadOnlyList<MatchFeedRow> Feed => _feedRows.Select(row => row.Row).ToList();

    /// <summary>
    /// The feed as the view binds it: the rows are built once per game and stay, a step only moves the marks
    /// (<see cref="MatchFeedItem.Current"/>) of the rows that change.
    /// </summary>
    public IReadOnlyList<MatchFeedItem> FeedRows { get => _feedRows; private set => Set(ref _feedRows, value); }

    /// <summary>The step's row in <see cref="FeedRows"/> (the view scrolls to it by this index), or -1 when the step has none.</summary>
    public int FeedCurrentIndex { get => _feedCurrentIndex; private set => Set(ref _feedCurrentIndex, value); }

    public IReadOnlyList<MatchDeckRow> LocalDeck { get => _localDeck; private set => Set(ref _localDeck, value); }

    public string LocalDeckNote { get => _localDeckNote; private set => Set(ref _localDeckNote, value); }

    public IReadOnlyList<MatchDeckRow> OpponentCards { get => _opponentCards; private set => Set(ref _opponentCards, value); }

    /// <summary>The meta list the opponent's archetype most likely played (a guess, marked as one).</summary>
    public IReadOnlyList<MatchDeckRow> OpponentPredicted { get => _opponentPredicted; private set => Set(ref _opponentPredicted, value); }

    public string OpponentPredictedNote { get => _opponentPredictedNote; private set => Set(ref _opponentPredictedNote, value); }

    /// <summary>Under the table: what the replay shows of the hidden cards (everything the log revealed, for a finished game).</summary>
    public string TableNote { get => _tableNote; private set => Set(ref _tableNote, value); }

    public string OpponentCardsNote { get => _opponentCardsNote; private set => Set(ref _opponentCardsNote, value); }

    /// <summary>What the opponent card list says when it is empty.</summary>
    public string OpponentCardsEmpty { get => _opponentCardsEmpty; private set => Set(ref _opponentCardsEmpty, value); }

    /// <summary>"Likely archetype: Zoolock (from the cards it played)" or a note that the call is not clear.</summary>
    public string ArchetypeLine { get => _archetypeLine; private set => Set(ref _archetypeLine, value); }

    public IReadOnlyList<MatchRoundView> Rounds { get => _rounds; private set => Set(ref _rounds, value); }

    public IReadOnlyList<MatchLobbyRow> Lobby { get => _lobby; private set => Set(ref _lobby, value); }

    /// <summary>The analysis tab (Constructed replays).</summary>
    public MatchAnalysis? Analysis { get => _analysis; private set => Set(ref _analysis, value); }

    /// <summary>The page being read: clears the previous game (and stops its playback).</summary>
    public void BeginLoading()
    {
        Clear();
        Loading = true;
    }

    /// <summary>
    /// Lets go of the shown game (its frames and lists): only the open match is kept in memory, so closing the page or
    /// opening another one frees the last.
    /// </summary>
    public void Clear()
    {
        Pause();
        _page = null;
        Loading = false;
        Header = null;
        Missing = string.Empty;
        Step = null;
        Actions = [];
        Turns = [];
        _turnsPage = null;
        _turnsCurrent = null;
        SetFeedRows([]);
        LocalDeck = [];
        OpponentCards = [];
        OpponentPredicted = [];
        OpponentPredictedNote = string.Empty;
        TableNote = string.Empty;
        OpponentCardsNote = string.Empty;
        OpponentCardsEmpty = string.Empty;
        Rounds = [];
        Lobby = [];
        Analysis = null;
        ArchetypeLine = string.Empty;
        LocalDeckNote = string.Empty;
        ClearPlayer();
        Raise(nameof(IsConstructed));
        Raise(nameof(IsBattlegrounds));
        Raise(nameof(CanShareDeck));
        RaiseTabs();
    }

    /// <summary>
    /// Shows a loaded game from its first step, from the player's side; with <paramref name="keepPosition"/> (the same game
    /// built again in another language) at the step, side and tab already on screen.
    /// </summary>
    public void Show(MatchPage page, bool keepPosition = false)
    {
        _page = page;
        if (keepPosition)
        {
            // The same game built again in another language: the tables are emptied first, so every card is read anew.
            Step = null;
            BattlegroundsStep = null;
        }

        if (!keepPosition)
        {
            Pause();
            _frame = 0;
            _bottomLocal = true;
            // A Battlegrounds game opens on its step-by-step replay when the file has one, else on its rounds.
            _tab = page.IsBattlegrounds && !page.HasBattlegroundsSteps ? "Rounds" : "Replay";
        }

        _frame = Math.Clamp(_frame, 0, FrameMax);
        Loading = false;
        Header = Masked(page);
        Missing = page.Missing ?? string.Empty;
        LocalDeck = page.LocalDeck;
        LocalDeckNote = page.LocalDeckNote;
        OpponentCards = page.OpponentCards;
        OpponentPredicted = page.OpponentPredicted;
        OpponentPredictedNote = page.OpponentPredictedNote;
        // A file of a finished game with the post-game part shows what the log revealed; an older one only what was seen.
        var revealed = page.Replay?.HasPostGame == true;
        TableNote = Loc.T(revealed ? "Match.View.RevealNote" : "Match.View.HiddenNote");
        OpponentCardsNote = Loc.T(revealed ? "Match.View.OpponentRevealedNote" : "Match.View.OpponentCardsNote");
        OpponentCardsEmpty = Loc.T(revealed ? "Match.View.NoReveals" : "Match.View.NoPlays");

        ArchetypeLine = page.OpponentArchetype is { } archetype
            ? Loc.F("Match.Deck.LikelyArchetype", archetype)
            : page.OpponentCards.Count > 0 ? Loc.T("Match.Deck.ArchetypeUnclear") : string.Empty;
        Rounds = page.Rounds;
        Lobby = page.Lobby;
        Analysis = page.Analysis;
        // The feed's texts once per game (another language builds the page again): -1 marks no row.
        SetFeedRows(page.HasBattlegroundsSteps ? [] : page.Feed(-1));
        ShowPlayer(page);
        Raise(nameof(IsConstructed));
        Raise(nameof(IsBattlegrounds));
        Raise(nameof(CanShareDeck));
        Raise(nameof(ShowOpponent));
        Raise(nameof(Frame));
        Raise(nameof(FrameMax));
        Raise(nameof(BottomLocal));
        Raise(nameof(BottomOpponent));
        Raise(nameof(Tab));
        RaiseTabs();
        Refresh();
        if (keepPosition)
        {
            Table.RaiseAll();
            BgTable.RaiseAll();
        }
    }

    private void Pick(bool chosen, string tab)
    {
        if (chosen)
        {
            Tab = tab;
        }
    }

    /// <summary>Ctrl+arrows and the outer buttons: the start of the next or previous turn (Battlegrounds: tavern).</summary>
    private void GoTurn(int direction)
    {
        if (_page is not null)
        {
            Frame = _page.TurnFrom(Frame, direction);
        }
    }

    /// <summary>The arrows and the middle buttons: the next or previous event, skipping turn starts (and combat deaths).</summary>
    private void GoEvent(int direction)
    {
        if (_page is not null)
        {
            Frame = _page.EventFrom(Frame, direction);
        }
    }

    private void Refresh()
    {
        if (_page is null)
        {
            return;
        }

        // The timeline first: the Battlegrounds list of events is the current stretch's.
        MoveTimeline();
        if (HasBattlegroundsSteps)
        {
            ShowBattlegroundsStep(_page.BattlegroundsStep(_frame));
        }
        else
        {
            Step = _page.Step(_frame, _bottomLocal ? ReplayPlayer.Local : ReplayPlayer.Opponent);
            // Read from the page when asked for: nothing on the Constructed table shows the turn's list.
            _actions = null;
            if (ActionRows.Count > 0)
            {
                ActionRows.Clear();
            }

            Raise(nameof(Actions));
            MoveFeed(_frame);
        }

        // The turn list is rebuilt only when the page or its current turn changes, not on every step of the same turn.
        var current = _page.TurnOf(_frame);
        if (!ReferenceEquals(_turnsPage, _page) || !Equals(_turnsCurrent, current))
        {
            _turnsPage = _page;
            _turnsCurrent = current;
            Turns = _page.Turns.Select(t => new MatchTurnRow(t.FirstFrame, t.Label, t.Tooltip, t.Local, t.Mulligan, t == current)).ToList();
        }
        MoveNavigation();
        Raise(nameof(Position));
    }

    /// <summary>
    /// Changes a list the view binds in place (<see cref="SyncList{T}"/>) and tells the view when it changed. A new list
    /// on every step made the items control build every row again: with the Battlegrounds table about 50 ms of the UI
    /// thread and 3.5 MB a step, 100 ms of CPU a second at x4 (docs/perf-architecture-audit.md, stage 10).
    /// </summary>
    private void Keep<T>(SyncList<T> list, IReadOnlyList<T> items, [CallerMemberName] string? name = null)
    {
        if (list.Sync(items))
        {
            Raise(name);
        }
    }

    /// <summary>The game's feed rows as built (none marked), and where each header's turn ends.</summary>
    private void SetFeedRows(IReadOnlyList<MatchFeedRow> rows)
    {
        _feedTurnEnds = new int[rows.Count];
        var end = -1;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Header)
            {
                _feedTurnEnds[i] = Math.Max(end, rows[i].Frame);
                end = -1;
            }
            else
            {
                end = Math.Max(end, rows[i].Frame);
            }
        }

        var items = new MatchFeedItem[rows.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new MatchFeedItem(rows[i]);
        }

        // The index first: the view scrolls to it when it changes, and by then the rows are the new game's.
        FeedCurrentIndex = -1;
        FeedRows = items;
        Raise(nameof(Feed));
    }

    /// <summary>
    /// Marks the step's row and the header of its turn (as <see cref="MatchPage.Feed"/> marks them) on the rows the view
    /// already shows: the two to four rows whose mark moves tell their bindings, the others are left alone.
    /// </summary>
    private void MoveFeed(int frame)
    {
        var index = -1;
        var moved = false;
        for (var i = 0; i < _feedRows.Count; i++)
        {
            var row = _feedRows[i];
            var current = row.Header ? frame >= row.Frame && frame <= _feedTurnEnds[i] : row.Frame == frame;
            if (current && !row.Header && index < 0)
            {
                index = i;
            }

            if (row.Current != current)
            {
                row.Current = current;
                moved = true;
            }
        }

        if (moved)
        {
            Raise(nameof(Feed));
        }

        FeedCurrentIndex = index;
    }

    private void RaiseTabs()
    {
        Raise(nameof(HasBattlegroundsSteps));
        Raise(nameof(HasPlayer));
        Raise(nameof(ShowReplay));
        Raise(nameof(ShowConstructedTable));
        Raise(nameof(ShowBattlegroundsTable));
        Raise(nameof(CanExportStep));
        Raise(nameof(ShowBattlegroundsOldNote));
        Raise(nameof(ShowSide));
        Raise(nameof(ShowDecks));
        Raise(nameof(ShowAnalysis));
        Raise(nameof(ShowRounds));
        Raise(nameof(ShowLobby));
    }
}
