using System.Globalization;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Live pipeline: consumes Hearthstone logs, polls client memory, records finished games and
/// publishes immutable <see cref="LiveSnapshot"/>s for the UI.
/// </summary>
public sealed partial class GameSession : ILogConsumer, IPowerListener
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the client must be closed, with no new Power.log lines, before the game it left is saved as
    /// <see cref="GameRecord.AbandonedResult"/>. A reconnect later on starts a new log (or a new CREATE_GAME) with another
    /// game key; <see cref="HistoryStore.Add"/> drops the abandoned row when that game is saved with its result.
    /// </summary>
    internal static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long Power.log must be quiet before a block left open at the end of a batch is completed.</summary>
    private static readonly TimeSpan QuietFlush = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a Battlegrounds game over for us waits for our final place before it is saved anyway.</summary>
    private static readonly TimeSpan FinalPlaceWait = TimeSpan.FromSeconds(2);

    private readonly HistoryStore _history;
    private readonly HearthstoneMemory _memory;
    private readonly ConstructedTracker _constructed = new();
    private readonly TurnClock _turnClock;
    private readonly BattlegroundsTracker _battlegrounds = new();
    private readonly GameFactsTracker _facts = new();
    private readonly ConstructedPicksTracker _picks = new();
    private readonly ReplayRecorder _replay = new();
    private readonly Lock _statsGate = new();

    // The watchdog can replace a wedged log watcher while its thread is still in here. This lock keeps the old and the
    // new thread from ever working on the parser at the same time.
    private readonly Lock _pipelineGate = new();
    private readonly MemoryPoller _memoryPoller;
    private int _gameGeneration;
    private IReadOnlyList<string>? _offeredHeroes;
    private int _offeredGeneration = -1;

    // The latest Battlegrounds view of the current game, its last non-empty board and every trinket offered.
    private BgSeen? _bgSeen;

    private sealed record BgSeen(int Generation, BattlegroundsView Last, IReadOnlyList<BgMinion> Board, IReadOnlyList<string> TrinketOffers);

    private volatile CardDb _cards = CardDb.Empty;
    private PowerParser? _parser;
    private bool _catchingUp;
    private string _scene = "UNKNOWN";

    private DeckDefinition? _queuedDeck;
    private string? _sessionDir;
    private DeckDefinition? _gameDeck;

    /// <summary>The game being tracked; internal for tests.</summary>
    internal GameState? Game => _parser?.Game;

    /// <summary>A multi-line block of the last batch is still open; internal for tests.</summary>
    internal bool ParserHasPending => _parser?.HasPending == true;

    /// <summary>The deck Decks.log announced for the next game; internal for tests.</summary>
    internal DeckDefinition? QueuedDeck => _queuedDeck;

    private string? _pendingDeckName;
    private long _pendingDeckId;
    private bool _readingDeck;

    private bool _recorded;

    /// <summary>When the Battlegrounds game was first seen over for us (our PLAYSTATE), for <see cref="FinalPlaceWait"/>.</summary>
    private DateTime? _localFinishedAt;
    private bool _recordedAbandoned;
    private bool _endingLeftGame;
    private DateTime _hearthstoneStoppedAt = DateTime.MinValue;
    private DateTime _lastPowerLinesAt = DateTime.MinValue;
    private BattlegroundsRating? _rating;
    private BattlegroundsRating? _ratingAtGameStart;
    private string? _awaitingRatingKey;
    private bool _awaitingDuos;
    private DateTime _awaitingRatingUntil;
    private bool _ranksRead;
    private bool _racesRead;

    private int? _sessionStartRating;
    private readonly List<int> _sessionPlacements = new();
    private int _sessionWins;
    private int _sessionLosses;
    private GameOutcome? _lastOutcome;

    private bool _hearthstoneRunning;
    private DateTime _nextProcessCheck;
    private DateTime _nextParseErrorNote;

    // Power.log lines the parser could not read, per exception type. The first of each type is logged with its stack;
    // later ones at most once a minute. The total shows in Settings -> About.
    private readonly Dictionary<string, int> _parseErrors = new(StringComparer.Ordinal);
    private int _parseErrorTotal;
    private int _parseErrorsSinceNote;

    // Diagnostics.
    private int _lastLoggedTurn = -1;
    private DateTime _nextClientConfigFix;
    private DateTime _nextHeartbeat;
    private int _publishCount;
    private long _slowestPublishMs;
    private DateTime _nextConfigCheck;
    private IReadOnlyList<string> _logConfigMissing = Array.Empty<string>();
    private bool _dirty = true;
    private DateTime _lastPublish;
    private Dictionary<string, HeroStats>? _heroStats;

    public GameSession(HistoryStore history, HearthstoneMemory memory)
    {
        _history = history;
        _memory = memory;
        // A history without a store of its own (tests) saves into the data folder's replays, as before.
        Replays = history.Replays ?? new ReplayStore(AppPaths.Replays);
        _memoryPoller = new MemoryPoller(memory);
        _turnClock = new TurnClock(() => _parser?.LastTimestamp);
        _history.Changed += () =>
        {
            lock (_statsGate)
            {
                _heroStats = null;
            }

            _ladderReload = true;
        };
        _battlegrounds.HeroStatsProvider = LookupHeroStats;
    }

    /// <summary>UTC clock of the tick loop; tests move it forward.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Whether the Hearthstone process runs; tests replace it.</summary>
    internal Func<bool> IsHearthstoneRunning { get; set; } = HearthstoneInstall.IsRunning;

    /// <summary>False in tests: a client running on the developer's machine must not leak its memory into them.</summary>
    internal bool ReadsMemory { get; set; } = true;

    /// <summary>Collects the garbage of a catch-up or a game once the log is quiet; tests pass one with a counter.</summary>
    internal MemoryTrimmer Trimmer { get; set; } = new();

    /// <summary>Off: every counter is built on every update instead of reusing the unchanged ones (tests compare both).</summary>
    internal bool ReuseCounterViews
    {
        get => _constructed.Counters.Views.Reuse;
        set => _constructed.Counters.Views.Reuse = value;
    }

    /// <summary>
    /// The host finished something that left garbage behind (start-up, a meta load): the memory is handed back once the
    /// app is quiet and no game is being played.
    /// </summary>
    public void RequestMemoryTrim() => Trimmer.NoteActivity(Clock());

    /// <summary>
    /// The player's own action left garbage (a meta load for a page or a filter, minimizing, closing or letting go of the
    /// main window): handed back once the window is minimized, closed or let go, not while the player looks at it (the
    /// collection pauses the app for 30–110 ms).
    /// </summary>
    /// <param name="windowLetGo">A main window was let go or closed: the collection runs the finalizers that free it.</param>
    public void RequestMemoryTrimWhenWindowAway(bool windowLetGo = false) => Trimmer.NoteActivityWhenWindowAway(Clock(), windowLetGo);

    /// <summary>
    /// The host's main window is on screen, or hidden in the tray and not let go yet; false when minimized, let go or
    /// not built (see <see cref="MemoryTrimmer.WindowInUse"/>).
    /// </summary>
    public bool MainWindowInUse
    {
        get => Trimmer.WindowInUse;
        set => Trimmer.WindowInUse = value;
    }

    public event Action<LiveSnapshot>? SnapshotChanged;

    /// <summary>Raised when a game result is final (rating included when available).</summary>
    public event Action<GameOutcome>? GameFinished;

    public LiveSnapshot Current { get; private set; } = LiveSnapshot.Empty;

    /// <summary>Hero, comp and shop recommendations layered onto the Battlegrounds view.</summary>
    public BattlegroundsAdvisor Advisor { get; } = new();

    public void SetMeta(BattlegroundsMeta? meta)
    {
        Advisor.Meta = meta;
        _dirty = true;
    }

    /// <summary>
    /// The comp the player chose; null clears it. In <see cref="CompMode.Manual"/> it is the only possible target and
    /// stays across games (the host also stores it in the settings); in <see cref="CompMode.Auto"/> it locks the
    /// advisor onto that comp until the next game, and null returns to automatic choice.
    /// </summary>
    public void PinComp(string? compId)
    {
        Advisor.PinnedCompId = compId;
        _dirty = true;
    }

    /// <summary>The tribe the player wants to build around (race key), or null for any.</summary>
    public void SetCompFocus(string? tribe)
    {
        if (Advisor.CompFocus == tribe)
        {
            return;
        }

        Advisor.CompFocus = tribe;
        _dirty = true;
    }

    /// <summary>
    /// Community Constructed stats for the mulligan panel; the host passes them after loading. Each format's stats are
    /// kept apart and a game uses its own format's.
    /// </summary>
    public void SetConstructedMeta(Constructed.ConstructedMeta? meta)
    {
        if (meta is null)
        {
            return;
        }

        _constructed.Meta = meta;
        _dirty = true;
    }

    /// <summary>
    /// Raised on the log thread when a Constructed game's format ("standard", "wild") has no stats yet; the host
    /// downloads them and passes them to <see cref="SetConstructedMeta"/>.
    /// </summary>
    public event Action<string>? ConstructedMetaNeeded
    {
        add => _constructed.MetaNeeded += value;
        remove => _constructed.MetaNeeded -= value;
    }

    /// <summary>The Hearthpulse API mulligan guide; the host passes it once at start-up. A finished lookup refreshes the overlay.</summary>
    public void SetMulliganGuide(MulliganGuideSource? guide)
    {
        if (_constructed.Guide is { } previous)
        {
            previous.Changed -= MarkDirty;
        }

        _constructed.Guide = guide;
        if (guide is not null)
        {
            guide.Changed += MarkDirty;
        }

        _dirty = true;
    }

    private void MarkDirty() => _dirty = true;

    /// <summary>
    /// Applies the "Game plan comp" setting; call at start-up and whenever settings change. Manual: the stored
    /// <paramref name="chosenCompId"/> becomes the choice. Switching to Auto drops the manual choice so the advisor
    /// picks again (the stored id is kept in the settings for a switch back).
    /// </summary>
    public void SetCompMode(CompMode mode, string? chosenCompId)
    {
        // The advisor is read on the log thread: both fields change together under its lock.
        lock (_pipelineGate)
        {
            var pinned = mode == CompMode.Manual ? chosenCompId : Advisor.CompMode == CompMode.Auto ? Advisor.PinnedCompId : null;
            if (Advisor.CompMode == mode && Advisor.PinnedCompId == pinned)
            {
                return;
            }

            Advisor.CompMode = mode;
            Advisor.PinnedCompId = pinned;
            _dirty = true;
        }
    }

    /// <summary>Hearthstone install folder, used to locate the client's log.config (it lives in the Wine prefix on Linux).</summary>
    public string? InstallDirectory { get; set; }

    public CardDb Cards
    {
        get => _cards;
        set
        {
            _cards = value;
            _battlegrounds.Cards = value;
            _dirty = true;
        }
    }

    // ---- IPowerListener -----------------------------------------------------------------------

    public void OnGameStarted(GameState game)
    {
        _gameGeneration++;
        _lastLoggedTurn = -1;
        if (!_catchingUp)
        {
            Diag.Info("Game started");
        }

        _recorded = false;
        _recordedAbandoned = false;
        _localFinishedAt = null;
        _ranksRead = false;
        _racesRead = false;
        if (Advisor.CompMode == CompMode.Auto)
        {
            // A lock lasts one game in Auto mode; a manual choice is kept until the player changes it.
            Advisor.PinnedCompId = null;
        }

        _gameDeck = _queuedDeck;
        _constructed.Deck = null;
        _ratingAtGameStart = _catchingUp ? null : _rating;
    }

    public void OnGameEnded(GameState game) => RecordIfFinished(game, force: true);

    // ---- internals ----------------------------------------------------------------------------

    private void AfterPowerBatch()
    {
        if (_parser?.Game is not { } game)
        {
            return;
        }

        if (game.Turn != _lastLoggedTurn)
        {
            // One line per turn shows exactly how far the tracker got before anything stopped.
            _lastLoggedTurn = game.Turn;
            if (!_catchingUp)
            {
                Diag.Info(game.IsBattlegrounds
                    ? $"BG turn {game.BattlegroundsTurn} (game turn {game.Turn}), {game.Entities.Count} entities"
                    : $"Turn {game.Turn}, {game.Entities.Count} entities");
            }
        }

        if (game.IsConstructedLike && _constructed.Deck is null && _gameDeck is not null && game.GameType.Length > 0)
        {
            _constructed.Deck = _gameDeck;
        }

        if (game.IsBattlegrounds && _battlegrounds.LocalFinished)
        {
            RecordWhenPlaceKnown(game);
        }
    }

    private void Publish()
    {
        var game = _parser?.Game;
        var inGame = game is not null && !game.IsComplete && game.GameType.Length > 0
                     && (_scene is "GAMEPLAY" or "UNKNOWN") && _hearthstoneRunning;
        ConstructedView? constructed = null;
        BattlegroundsView? battlegrounds = null;

        try
        {
            if (inGame && game!.IsBattlegrounds)
            {
                battlegrounds = _battlegrounds.BuildView(game);
                if (battlegrounds.IsHeroSelection && battlegrounds.HeroOptions.Count > 1)
                {
                    _offeredHeroes = battlegrounds.HeroOptions.Select(h => h.CardId).ToList();
                    _offeredGeneration = _gameGeneration;
                }
                else if (!battlegrounds.IsHeroSelection)
                {
                    var seen = _bgSeen is { } s && s.Generation == _gameGeneration ? s : new BgSeen(_gameGeneration, battlegrounds, [], []);
                    _bgSeen = seen with
                    {
                        Last = battlegrounds,
                        Board = battlegrounds.Board.Count > 0 ? battlegrounds.Board : seen.Board,
                        TrinketOffers = battlegrounds.TrinketOptions.Count > 1
                            ? seen.TrinketOffers.Concat(battlegrounds.TrinketOptions.Select(t => t.CardId)).Distinct(StringComparer.Ordinal).ToList()
                            : seen.TrinketOffers,
                    };
                }

                var rating = _rating is null ? (int?)null : game.IsDuos ? _rating.Duos : _rating.Solo;
                // The game's start marks it for the daily free limit of the stats pool (in memory only).
                Advisor.Game = game.StartedAt;
                battlegrounds = Advisor.Enrich(battlegrounds, rating, _cards);
                // After the copy kept for the finished game above: the names and ratings stay in the live view only.
                battlegrounds = WithOpponentMmr(battlegrounds, game);
            }
            else if (inGame && game!.IsConstructedLike && game.LocalPlayerId != 0)
            {
                constructed = _constructed.BuildView(game, _cards);
                constructed = WithCardPool(constructed, game) with { Turns = _turnClock.View(game) };
                constructed = WithSecrets(constructed, game);
                constructed = WithGraveyard(constructed, game);
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Building view failed", ex);
        }

        var session = new SessionSummary(
            _sessionStartRating,
            _rating is { Solo: > 0 } r ? r.Solo : null,
            _sessionWins + _sessionLosses,
            _sessionPlacements.ToList(),
            _sessionWins,
            _sessionLosses);

        Current = new LiveSnapshot(
            _scene,
            _hearthstoneRunning,
            _memory.IsAttached,
            _memory.Status,
            inGame ? game!.GameType : null,
            inGame,
            constructed,
            battlegrounds,
            _rating,
            session,
            _logConfigMissing,
            _parseErrorTotal == 0 ? null : new ParseErrorSummary(_parseErrorTotal, ParseErrorText()))
        {
            ArenaDraft = BuildArenaDraft(),
            Ladder = BuildLadder(game, inGame),
        };

        SnapshotChanged?.Invoke(Current);
    }
}
