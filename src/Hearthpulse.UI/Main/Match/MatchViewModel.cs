using Hearthpulse.UI.Infrastructure;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.UI.Main.Match;

/// <summary>A chip of the turn strip; <see cref="Selected"/> holds the step on screen.</summary>
public sealed record MatchTurnRow(int FirstFrame, string Label, string Tooltip, bool Local, bool Mulligan, bool Selected);

/// <summary>
/// The match page (opened from History): header, the step-by-step replay seen from either side, both decks, the
/// Battlegrounds rounds and lobby. Everything shown comes from <see cref="MatchPage"/> in Core; this only keeps the
/// position (step, side, tab).
/// </summary>
public sealed class MatchViewModel : Observable
{
    private MatchPage? _page;
    private int _frame;
    private bool _bottomLocal = true;
    private string _tab = "Replay";
    private bool _loading;
    private MatchHeader? _header;
    private string _missing = string.Empty;
    private MatchStepView? _step;
    private IReadOnlyList<MatchActionRow> _actions = [];
    private IReadOnlyList<MatchTurnRow> _turns = [];
    private IReadOnlyList<MatchDeckRow> _localDeck = [];
    private IReadOnlyList<MatchDeckRow> _opponentCards = [];
    private IReadOnlyList<MatchRoundView> _rounds = [];
    private IReadOnlyList<MatchLobbyRow> _lobby = [];
    private string _localDeckNote = string.Empty;
    private string _archetypeLine = string.Empty;
    private MatchAnalysis? _analysis;

    public MatchViewModel(MainViewModel owner)
    {
        Back = new RelayCommand(owner.CloseMatch);
        Previous = new RelayCommand(() => Frame--);
        Next = new RelayCommand(() => Frame++);
        First = new RelayCommand(() => Frame = 0);
        Last = new RelayCommand(() => Frame = FrameMax);
        PreviousTurn = new RelayCommand(() => GoTurn(-1));
        NextTurn = new RelayCommand(() => GoTurn(+1));
        GoFrame = new RelayCommand(p => Frame = p switch
        {
            MatchTurnRow turn => turn.FirstFrame,
            MatchActionRow action => action.Frame,
            int frame => frame,
            _ => Frame,
        });
        SetSide = new RelayCommand(p =>
        {
            if (p as string == "Opponent")
            {
                BottomOpponent = true;
            }
            else
            {
                BottomLocal = true;
            }
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

    /// <summary>Goes to a turn chip, an action row or a step number.</summary>
    public RelayCommand GoFrame { get; }

    /// <summary>"Local" or "Opponent": whose side is at the bottom.</summary>
    public RelayCommand SetSide { get; }

    /// <summary>"Replay", "Decks", "Analysis" (Constructed); "Rounds", "Lobby" (Battlegrounds).</summary>
    public RelayCommand SetTab { get; }

    /// <summary>True while the game's replay is being read.</summary>
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    public MatchHeader? Header { get => _header; private set => Set(ref _header, value); }

    /// <summary>Why there is nothing to replay, or empty.</summary>
    public string Missing { get => _missing; private set => Set(ref _missing, value); }

    public bool IsConstructed => _page is { Replay: not null };

    public bool IsBattlegrounds => _page is { Battlegrounds: not null };

    public bool ShowOpponent => _page is { IsBattlegrounds: false };

    public string Tab
    {
        get => _tab;
        private set
        {
            if (Set(ref _tab, value))
            {
                RaiseTabs();
            }
        }
    }

    // The tab and side radios bind two-way: a radio being unchecked by its group sets false, which is ignored.
    public bool ShowReplay { get => IsConstructed && Tab == "Replay"; set => Pick(value, "Replay"); }

    public bool ShowDecks { get => IsConstructed && Tab == "Decks"; set => Pick(value, "Decks"); }

    public bool ShowAnalysis { get => IsConstructed && Tab == "Analysis"; set => Pick(value, "Analysis"); }

    public bool ShowRounds { get => IsBattlegrounds && Tab != "Lobby"; set => Pick(value, "Rounds"); }

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

    public MatchStepView? Step { get => _step; private set => Set(ref _step, value); }

    public string Position => _page is null ? string.Empty : $"Step {Frame + 1} of {FrameMax + 1}";

    public IReadOnlyList<MatchActionRow> Actions { get => _actions; private set => Set(ref _actions, value); }

    public IReadOnlyList<MatchTurnRow> Turns { get => _turns; private set => Set(ref _turns, value); }

    public IReadOnlyList<MatchDeckRow> LocalDeck { get => _localDeck; private set => Set(ref _localDeck, value); }

    public string LocalDeckNote { get => _localDeckNote; private set => Set(ref _localDeckNote, value); }

    public IReadOnlyList<MatchDeckRow> OpponentCards { get => _opponentCards; private set => Set(ref _opponentCards, value); }

    /// <summary>"Likely archetype: Zoolock (from the cards it played)" or a note that the call is not clear.</summary>
    public string ArchetypeLine { get => _archetypeLine; private set => Set(ref _archetypeLine, value); }

    public IReadOnlyList<MatchRoundView> Rounds { get => _rounds; private set => Set(ref _rounds, value); }

    public IReadOnlyList<MatchLobbyRow> Lobby { get => _lobby; private set => Set(ref _lobby, value); }

    /// <summary>The analysis tab (Constructed replays).</summary>
    public MatchAnalysis? Analysis { get => _analysis; private set => Set(ref _analysis, value); }

    /// <summary>The page being read: clears the previous game.</summary>
    public void BeginLoading()
    {
        Loading = true;
        Missing = string.Empty;
    }

    /// <summary>Shows a loaded game from its first step, from the player's side.</summary>
    public void Show(MatchPage page)
    {
        _page = page;
        _frame = 0;
        _bottomLocal = true;
        _tab = page.IsBattlegrounds ? "Rounds" : "Replay";
        Loading = false;
        Header = page.Header;
        Missing = page.Missing ?? string.Empty;
        LocalDeck = page.LocalDeck;
        LocalDeckNote = page.LocalDeckNote;
        OpponentCards = page.OpponentCards;
        ArchetypeLine = page.OpponentArchetype is { } archetype
            ? $"Likely archetype: {archetype}"
            : page.OpponentCards.Count > 0 ? "Archetype: not clear from the cards it played" : string.Empty;
        Rounds = page.Rounds;
        Lobby = page.Lobby;
        Analysis = page.Analysis;
        Raise(nameof(IsConstructed));
        Raise(nameof(IsBattlegrounds));
        Raise(nameof(ShowOpponent));
        Raise(nameof(Frame));
        Raise(nameof(FrameMax));
        Raise(nameof(BottomLocal));
        Raise(nameof(BottomOpponent));
        Raise(nameof(Tab));
        RaiseTabs();
        Refresh();
    }

    private void Pick(bool chosen, string tab)
    {
        if (chosen)
        {
            Tab = tab;
        }
    }

    private void GoTurn(int direction)
    {
        if (_page?.TurnOf(Frame) is not { } current)
        {
            return;
        }

        var turns = _page.Turns;
        var index = turns.ToList().IndexOf(current) + direction;
        if (index >= 0 && index < turns.Count)
        {
            Frame = turns[index].FirstFrame;
        }
    }

    private void Refresh()
    {
        if (_page is null)
        {
            return;
        }

        Step = _page.Step(_frame, _bottomLocal ? ReplayPlayer.Local : ReplayPlayer.Opponent);
        Actions = _page.Actions(_frame);
        var current = _page.TurnOf(_frame);
        Turns = _page.Turns.Select(t => new MatchTurnRow(t.FirstFrame, t.Label, t.Tooltip, t.Local, t.Mulligan, t == current)).ToList();
        Raise(nameof(Position));
    }

    private void RaiseTabs()
    {
        Raise(nameof(ShowReplay));
        Raise(nameof(ShowDecks));
        Raise(nameof(ShowAnalysis));
        Raise(nameof(ShowRounds));
        Raise(nameof(ShowLobby));
    }
}
