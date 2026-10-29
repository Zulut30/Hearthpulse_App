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

    private readonly HistoryStore _history;
    private readonly HearthstoneMemory _memory;
    private readonly ConstructedTracker _constructed = new();
    private readonly TurnClock _turnClock;
    private readonly BattlegroundsTracker _battlegrounds = new();
    private readonly GameFactsTracker _facts = new();
    private readonly ConstructedPicksTracker _picks = new();
    private readonly ReplayRecorder _replay = new();
    private readonly object _statsGate = new();

    // The watchdog can replace a wedged log watcher while its thread is still in here. This lock keeps the old and the
    // new thread from ever working on the parser at the same time.
    private readonly object _pipelineGate = new();
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

    // ---- ILogConsumer -------------------------------------------------------------------------

    public void OnSessionStarted(string sessionDir, DateTime sessionStart, bool attachedMidSession)
    {
        lock (_pipelineGate)
        {
            OnSessionStartedCore(sessionDir, sessionStart, attachedMidSession);
        }
    }

    private void OnSessionStartedCore(string sessionDir, DateTime sessionStart, bool attachedMidSession)
    {
        _gameGeneration++;
        // The same folder again is a restarted watcher joining the session at its last game: the game goes on and is
        // replayed from its start, as in a resync.
        var sameSession = _parser is not null && string.Equals(sessionDir, _sessionDir, StringComparison.OrdinalIgnoreCase);
        _sessionDir = sessionDir;
        if (sameSession)
        {
            _parser!.ResetGame();
        }

        // A new client session: a game the old one never finished was left (client closed or crashed).
        _endingLeftGame = true;
        try
        {
            _parser?.AbandonCurrentGame();
        }
        finally
        {
            _endingLeftGame = false;
        }

        _parser = new PowerParser(sessionStart);
        OnLadderLogSession(sessionStart);
        _parser.AddListener(_constructed);
        _parser.AddListener(_constructed.Counters);
        _parser.AddListener(_battlegrounds);
        _parser.AddListener(_battlegrounds.Counters);
        _parser.AddListener(_facts);
        _parser.AddListener(_cardPool);
        _parser.AddListener(_picks);
        _parser.AddListener(_secrets);
        _parser.AddListener(_replay);
        _parser.AddListener(_turnClock);
        _parser.AddListener(this);
        _scene = "UNKNOWN";
        _arenaDraft.Reset();
        _dirty = true;
        Diag.Info($"Log session {Path.GetFileName(sessionDir)} (mid-session: {attachedMidSession})");
    }

    /// <summary>
    /// The watcher is replaying the current game from its start: drop the tracked copy without recording it as
    /// finished. The replayed CREATE_GAME line rebuilds the parser, tracker and view from scratch.
    /// </summary>
    public void OnResync()
    {
        lock (_pipelineGate)
        {
            OnResyncCore();
        }
    }

    private void OnResyncCore()
    {
        _gameGeneration++;
        _parser?.ResetGame();
        _scene = "UNKNOWN";
        _dirty = true;
    }

    public void OnLines(string fileName, IReadOnlyList<string> lines, bool catchingUp)
    {
        lock (_pipelineGate)
        {
            OnLinesCore(fileName, lines, catchingUp);
        }
    }

    private void OnLinesCore(string fileName, IReadOnlyList<string> lines, bool catchingUp)
    {
        _catchingUp = catchingUp;
        switch (fileName)
        {
            case "Power.log":
                foreach (var line in lines)
                {
                    // One line the parser does not expect (a new client format) must cost that line only. Before, the
                    // exception dropped the rest of the batch, the game state went wrong and the overlay looked frozen.
                    try
                    {
                        _parser?.Feed(line);
                    }
                    catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException
                                                   or InvalidOperationException or IndexOutOfRangeException
                                                   or KeyNotFoundException or NullReferenceException)
                    {
                        NoteParseError(line, ex);
                    }
                }

                if (lines.Count > 0)
                {
                    _lastPowerLinesAt = Clock();
                    Trimmer.NoteActivity(_lastPowerLinesAt);
                }

                // Hearthstone writes the log in chunks, so a batch can end inside a FULL_ENTITY's "tag=" lines or a
                // choice's options. Flushing here dropped the rest of them (thousands of tag values per session in
                // small batches). The block is completed by its next line, or by OnTick once the log is quiet.
                AfterPowerBatch();
                break;
            case "LoadingScreen.log":
                foreach (var line in lines) HandleLoadingScreen(line);
                break;
            case "Decks.log":
                foreach (var line in lines) HandleDecks(line);
                break;
            case "Arena.log":
                foreach (var line in lines) HandleArena(line);
                break;
        }

        _catchingUp = false;
        _dirty = true;
    }

    /// <summary>
    /// Counts a Power.log line the parser could not read. The first exception of each type is logged with its stack, so
    /// a new client format shows where it broke; later ones are summed up at most once a minute.
    /// </summary>
    private void NoteParseError(string line, Exception ex)
    {
        var type = ex.GetType().Name;
        _parseErrors[type] = _parseErrors.GetValueOrDefault(type) + 1;
        _parseErrorTotal++;
        _parseErrorsSinceNote++;
        _dirty = true;
        var text = line[..Math.Min(line.Length, 200)];
        if (_parseErrors[type] == 1)
        {
            Diag.Error($"Skipped a Power.log line the parser could not read (first {type}): {text}", ex);
        }
        else if (Clock() >= _nextParseErrorNote)
        {
            Diag.Info($"Skipped {_parseErrorsSinceNote} more Power.log line(s) the parser could not read ({ParseErrorText()}); last {type}: {text}");
            _parseErrorsSinceNote = 0;
        }
        else
        {
            return;
        }

        _nextParseErrorNote = Clock().AddMinutes(1);
    }

    private string ParseErrorText() => string.Join(", ", _parseErrors.OrderByDescending(e => e.Value).Select(e => $"{e.Key} ×{e.Value}"));

    /// <summary>How long Power.log must have been quiet, with the game closed, before the watcher slows down.</summary>
    private static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The game is closed and Power.log has been quiet for a while. A process check that misses a running client (an
    /// unusual install) costs at most a second: its lines make this false again. Read on the watcher thread.
    /// </summary>
    public bool Quiet => !_hearthstoneRunning && Clock() - _lastPowerLinesAt >= QuietAfter;

    public void OnTick()
    {
        DateTime now;
        bool gameInProgress;
        lock (_pipelineGate)
        {
            (now, gameInProgress) = OnTickCore();
        }

        // Last and outside the lock: a collection never delays this tick's view, and the pipeline and the UI's calls into
        // the session do not wait on it (after a window is let go it also waits for the finalizers). A game in progress
        // (not abandoned) puts it off.
        Trimmer.Tick(now, busy: gameInProgress);
    }

    /// <returns>The tick's time and whether a game is in progress, for the memory trim after it.</returns>
    private (DateTime Now, bool GameInProgress) OnTickCore()
    {
        var now = Clock();
        if (_parser is { HasPending: true } parser && now - _lastPowerLinesAt >= QuietFlush)
        {
            parser.Flush();
            AfterPowerBatch();
            _dirty = true;
        }

        if (now >= _nextProcessCheck)
        {
            _nextProcessCheck = now.AddSeconds(2);
            var running = IsHearthstoneRunning();
            if (running != _hearthstoneRunning)
            {
                _hearthstoneRunning = running;
                _hearthstoneStoppedAt = running ? DateTime.MinValue : now;
                _dirty = true;
            }

            AbandonIfClientGone(now);
        }

        if (now >= _nextConfigCheck)
        {
            _nextConfigCheck = now.AddSeconds(30);
            _logConfigMissing = LogConfig.MissingSections(InstallDirectory);
            if (ClientConfig.LimitActive(InstallDirectory))
            {
                _logConfigMissing = [.. _logConfigMissing, Loc.T("Core.Session.LogSizeLimit")];
                if (now >= _nextClientConfigFix && ClientConfig.LogSizeLimit(InstallDirectory) is not -1)
                {
                    // Removed again (a game repair, another tool): put it back so the next client start has no limit.
                    _nextClientConfigFix = now.AddMinutes(10);
                    LogConfig.EnsureAll(InstallDirectory);
                }
            }
        }

        // The memory is read on its own thread; this thread only says what it needs and applies what came back.
        _memoryPoller.Post(_hearthstoneRunning && ReadsMemory ? BuildMemoryRequest() : null);
        if (_memoryPoller.Take() is { } reading)
        {
            ApplyMemory(reading);
        }

        TickSecrets(now);

        if (_awaitingRatingKey is not null && now > _awaitingRatingUntil)
        {
            _awaitingRatingKey = null;
        }

        if (_dirty && now - _lastPublish >= PublishInterval)
        {
            _dirty = false;
            _lastPublish = now;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Publish();
            var took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _publishCount++;
            _slowestPublishMs = Math.Max(_slowestPublishMs, took);
            if (took > 250)
            {
                Diag.Info($"Building the overlay data took {took:N0} ms (turn {_parser?.Game?.Turn})");
            }
        }

        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now.AddSeconds(15);
            var g = _parser?.Game;
            var game = g is null
                ? "none"
                : $"{(g.GameType.Length > 0 ? g.GameType : "?")} turn {g.Turn}{(g.IsBattlegrounds ? $" (BG turn {g.BattlegroundsTurn})" : string.Empty)}, "
                  + $"{(g.IsComplete ? "over" : _recordedAbandoned ? "abandoned" : "live")}, {g.Entities.Count} entities";
            Diag.Debug($"Tracker: scene {_scene}, Hearthstone {(_hearthstoneRunning ? "running" : "not running")}, game {game}, "
                       + $"{_publishCount} updates (slowest {_slowestPublishMs:N0} ms), memory: {_memory.Status}");
            _publishCount = 0;
            _slowestPublishMs = 0;
        }

        return (now, _parser?.Game is { IsComplete: false } && !_recordedAbandoned);
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
            RecordIfFinished(game, force: false);
        }
    }

    /// <summary>
    /// The client has been closed for <see cref="AbandonAfter"/> without finishing the game: save it as abandoned instead
    /// of keeping it "live" until the next client start (or forever, when the app closes first).
    /// </summary>
    private void AbandonIfClientGone(DateTime now)
    {
        if (_hearthstoneRunning || _recorded || _recordedAbandoned || _parser?.Game is not { IsComplete: false } game
            || game.GameType.Length == 0)
        {
            return;
        }

        var quietSince = _hearthstoneStoppedAt > _lastPowerLinesAt ? _hearthstoneStoppedAt : _lastPowerLinesAt;
        if (now - quietSince < AbandonAfter)
        {
            return;
        }

        Diag.Info($"Hearthstone closed during a game ({game.GameType}, turn {game.Turn}) and did not come back: saving it as abandoned.");
        RecordIfFinished(game, force: true);
    }

    private void RecordIfFinished(GameState game, bool force)
    {
        if (_recorded || (!force && !_battlegrounds.LocalFinished))
        {
            return;
        }

        // A game that never reached its end is kept as abandoned (no result) when the local player had none yet; if it
        // ends after all (the client came back to the same log), the real result replaces that row.
        var abandoned = !game.IsComplete || _endingLeftGame;
        var duos = game.IsDuos;
        int? before = _ratingAtGameStart is { } r ? (duos ? r.Duos : r.Solo) : null;
        var rank = _constructed.LocalRank?.Text;
        var races = _battlegrounds.MemoryRaces is { Count: > 0 } list ? string.Join(",", list) : null;
        var record = GameRecords.Build(game, _battlegrounds, _cards, _constructed.Deck, "live", before is > 0 ? before : null, null, rank, races,
            _facts, _constructed.LocalRank, abandoned, _constructed.MetaFor(game.FormatType));
        if (record is null)
        {
            if (abandoned) _recordedAbandoned = true;
            else _recorded = true;
            return;
        }

        if (record.IsAbandoned)
        {
            if (_recordedAbandoned)
            {
                return;
            }

            _recordedAbandoned = true;
        }
        else
        {
            _recorded = true;
        }

        record = SaveReplay(record, game);
        Diag.Info($"Game {(record.IsAbandoned ? "abandoned" : "finished")}: {record.Mode}, {record.Result}, place {record.Placement}, {record.Turns} turns{(_catchingUp ? " (replayed)" : string.Empty)}");
        try
        {
            _history.Add(record);
        }
        catch (Exception ex)
        {
            Diag.Error("Saving game failed", ex);
        }

        // An abandoned game has no outcome: it does not count in this session's results and is not announced.
        if (_catchingUp || record.IsAbandoned)
        {
            _dirty = true;
            return;
        }

        if (record.IsBattlegrounds)
        {
            _sessionPlacements.Add(record.Placement);
            if (before is > 0)
            {
                _awaitingRatingKey = record.Key;
                _awaitingDuos = duos;
                _awaitingRatingUntil = Clock().AddSeconds(120);
                if (_hearthstoneRunning && ReadsMemory)
                {
                    _memoryPoller.Post(BuildMemoryRequest());
                    _memoryPoller.Wake();
                }
            }
        }

        if (record.Result == "Win") _sessionWins++;
        else if (record.Result == "Loss") _sessionLosses++;

        var offered = _offeredGeneration == _gameGeneration ? _offeredHeroes : null;
        var details = record.IsBattlegrounds && _bgSeen is { } seen && seen.Generation == _gameGeneration
            ? new BgGameDetails(
                record.Turns > 0 ? record.Turns : seen.Last.Turn,
                record.TavernTier > 0 ? record.TavernTier : seen.Last.TechLevel,
                seen.Last.Triples,
                seen.Last.AnomalyCardId,
                seen.Last.Races,
                seen.Board,
                seen.TrinketOffers,
                seen.Last.Trinkets,
                seen.Last.TierHistory,
                seen.Last.Combats) { Choices = seen.Last.Choices, Plays = seen.Last.Plays }
            : null;
        _lastOutcome = new GameOutcome(record.Mode, record.Result, record.Placement, record.RatingBefore, null, record.HeroCardId, offered)
        {
            Details = details,
            Key = record.Key,
            GameType = record.GameType,
            Format = record.Format,
            ConstructedPicks = record.IsBattlegrounds ? [] : _picks.Picks,
        };
        GameFinished?.Invoke(_lastOutcome);
        _dirty = true;
    }

    private MemoryRequest BuildMemoryRequest()
    {
        var game = _parser?.Game;
        var inGame = game is { IsComplete: false };
        var battlegrounds = inGame && game!.IsBattlegrounds;
        return new MemoryRequest(
            _gameGeneration,
            inGame,
            battlegrounds,
            ReadRating: !inGame || battlegrounds || _scene == "BACON",
            // Lobby tribes are set before hero selection, which is when the hero advisor needs them.
            WantRaces: battlegrounds && !_racesRead,
            WantRanks: inGame && game!.IsConstructedLike && !_ranksRead && game.Turn >= 1,
            _awaitingRatingKey,
            WantDraftChoices: WantDraftChoices,
            WantLobbyNames: battlegrounds && WantLobbyNames);
    }

    /// <summary>Applies a memory reading on the tracking thread. Values read for an earlier game are dropped.</summary>
    private void ApplyMemory(MemoryReading reading)
    {
        ApplyArenaMemory(reading);
        var request = reading.Request;
        if (_awaitingRatingKey is not null && request.AwaitingRatingKey == _awaitingRatingKey)
        {
            ApplyPostGameRating(reading.PostGameRating, reading.Rating);
        }

        if (request.ReadRating && reading.Rating is { } rating && rating != _rating)
        {
            _rating = rating;
            _sessionStartRating ??= rating.Solo > 0 ? rating.Solo : null;
            _dirty = true;
        }

        var game = _parser?.Game;
        if (game is null || request.Generation != _gameGeneration)
        {
            return;
        }

        ApplyLobbyMemory(reading);
        if (request.WantRaces && !_racesRead)
        {
            var raceIds = reading.Races;
            if (raceIds is { Count: >= 4 })
            {
                _racesRead = true;
                _battlegrounds.MemoryRaces = raceIds
                    .Select(id => Races.ById.GetValueOrDefault(id, $"RACE_{id}"))
                    .OrderBy(r => r, StringComparer.Ordinal)
                    .ToList();
                _dirty = true;
            }
        }

        // The buttons are the authority for shop costs: they carry hero-power and trinket discounts.
        if (request.InGame && request.Battlegrounds && reading.ButtonCosts != _battlegrounds.LiveButtonCosts)
        {
            _battlegrounds.LiveButtonCosts = reading.ButtonCosts;
            _dirty = true;
        }

        if (request.WantRanks && !_ranksRead)
        {
            if (reading.Players is { } p)
            {
                _ranksRead = true;
                _constructed.LocalRank = PickRank(p.Local, game.FormatType);
                _constructed.OpponentRank = PickRank(p.Opponent, game.FormatType);
                _dirty = true;
            }
        }
    }

    private void ApplyPostGameRating(int? postGameRating, BattlegroundsRating? current)
    {
        var before = _ratingAtGameStart is { } r ? (_awaitingDuos ? r.Duos : r.Solo) : 0;
        int? after = postGameRating;
        if (after is null)
        {
            var value = current is null ? 0 : _awaitingDuos ? current.Duos : current.Solo;
            if (value > 0 && value != before)
            {
                after = value;
            }
        }

        if (after is > 0 && after != before)
        {
            try
            {
                _history.UpdateRatingAfter(_awaitingRatingKey!, after.Value);
            }
            catch (Exception ex)
            {
                Diag.Error("Saving rating failed", ex);
            }

            if (_lastOutcome is not null)
            {
                _lastOutcome = _lastOutcome with { RatingAfter = after };
                GameFinished?.Invoke(_lastOutcome);
            }

            _rating = _awaitingDuos
                ? (_rating ?? new BattlegroundsRating(0, 0)) with { Duos = after.Value }
                : (_rating ?? new BattlegroundsRating(0, 0)) with { Solo = after.Value };
            _awaitingRatingKey = null;
            _dirty = true;
        }
        else if (Clock() > _awaitingRatingUntil)
        {
            _awaitingRatingKey = null;
        }
    }

    private static RankInfo? PickRank(MatchPlayerInfo? player, string format) => format switch
    {
        "FT_STANDARD" => player?.Standard,
        "FT_WILD" => player?.Wild,
        "FT_CLASSIC" => player?.Classic,
        "FT_TWIST" => player?.Twist,
        _ => null,
    };

    private HeroStats? LookupHeroStats(string baseHeroId)
    {
        lock (_statsGate)
        {
            if (_heroStats is null)
            {
                try
                {
                    var cards = _cards;
                    _heroStats = _history.HeroStats(cards.BaseHeroId)
                        .ToDictionary(h => h.HeroCardId, h => new HeroStats(h.Games, h.AveragePlace, h.Top4Rate, h.WinRate));
                }
                catch (Exception ex)
                {
                    Diag.Error("Hero stats query failed", ex);
                    _heroStats = new Dictionary<string, HeroStats>();
                }
            }

            return _heroStats.GetValueOrDefault(baseHeroId);
        }
    }

    private void HandleLoadingScreen(string line)
    {
        var match = SceneRegex().Match(line);
        if (match.Success)
        {
            _scene = match.Groups["mode"].Value;
        }
    }

    private void HandleDecks(string line)
    {
        if (DecksContent(line) is not { } content)
        {
            return;
        }

        if (content.EndsWith("With Deck:", StringComparison.Ordinal))
        {
            _readingDeck = true;
            _pendingDeckName = null;
            _pendingDeckId = 0;
            return;
        }

        if (!_readingDeck)
        {
            return;
        }

        if (content.StartsWith("### ", StringComparison.Ordinal))
        {
            _pendingDeckName = content[4..];
        }
        else if (content.StartsWith("# Deck ID: ", StringComparison.Ordinal))
        {
            long.TryParse(content.AsSpan(11), out _pendingDeckId);
        }
        else if (!content.StartsWith('#'))
        {
            _readingDeck = false;
            if (DeckString.TryDecode(content, _pendingDeckName ?? "Deck", _pendingDeckId) is { } deck)
            {
                _queuedDeck = deck;
            }
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
                battlegrounds = Advisor.Enrich(battlegrounds, rating, _cards);
                // After the copy kept for the finished game above: the names and ratings stay in the live view only.
                battlegrounds = WithOpponentMmr(battlegrounds, game);
            }
            else if (inGame && game!.IsConstructedLike && game.LocalPlayerId != 0)
            {
                constructed = _constructed.BuildView(game, _cards);
                constructed = WithCardPool(constructed, game) with { Turns = _turnClock.View(game) };
                constructed = WithSecrets(constructed, game);
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
            _lastOutcome,
            _logConfigMissing,
            _parseErrorTotal == 0 ? null : new ParseErrorSummary(_parseErrorTotal, ParseErrorText()))
        {
            ArenaDraft = BuildArenaDraft(),
            Ladder = BuildLadder(game, inGame),
        };

        SnapshotChanged?.Invoke(Current);
    }

    [GeneratedRegex(@"LoadingScreen\.OnSceneLoaded\(\) - prevMode=\w+ currMode=(?<mode>\w+)")]
    private static partial Regex SceneRegex();

    /// <summary>
    /// The text of a Decks.log line. Current clients write "I 12:21:51.2461640 Finding Game With Deck:" (level, time,
    /// text); older ones put a method name and " - " before the text. Deck names may contain " - " themselves, so only
    /// a method-like prefix is cut.
    /// </summary>
    internal static string? DecksContent(string line)
    {
        var match = DecksPrefixRegex().Match(line);
        return match.Success ? line[match.Length..].Trim() : null;
    }

    [GeneratedRegex(@"^[A-Z] \d{1,2}:\d{2}:\d{2}(?:\.\d+)? (?:[\w.]+\(\) - )?")]
    private static partial Regex DecksPrefixRegex();
}
