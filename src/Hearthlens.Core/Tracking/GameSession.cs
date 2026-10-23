using System.Text.RegularExpressions;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Hearthstone;
using Hearthlens.Core.Logs;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;

namespace Hearthlens.Core.Tracking;

/// <summary>
/// Live pipeline: consumes Hearthstone logs, polls client memory, records finished games and
/// publishes immutable <see cref="LiveSnapshot"/>s for the UI.
/// </summary>
public sealed partial class GameSession : ILogConsumer, IPowerListener
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(100);

    private readonly HistoryStore _history;
    private readonly HearthstoneMemory _memory;
    private readonly ConstructedTracker _constructed = new();
    private readonly BattlegroundsTracker _battlegrounds = new();
    private readonly object _statsGate = new();

    // The watchdog can replace a wedged log watcher while its thread is still in here. This lock keeps the old and the
    // new thread from ever working on the parser at the same time.
    private readonly object _pipelineGate = new();
    private readonly MemoryPoller _memoryPoller;
    private int _gameGeneration;

    private volatile CardDb _cards = CardDb.Empty;
    private PowerParser? _parser;
    private bool _catchingUp;
    private string _scene = "UNKNOWN";

    private DeckDefinition? _queuedDeck;
    private DeckDefinition? _gameDeck;
    private string? _pendingDeckName;
    private long _pendingDeckId;
    private bool _readingDeck;

    private bool _recorded;
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
        _history.Changed += () =>
        {
            lock (_statsGate)
            {
                _heroStats = null;
            }
        };
        _battlegrounds.HeroStatsProvider = LookupHeroStats;
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
    /// Applies the "Game plan comp" setting; call at start-up and whenever settings change. Manual: the stored
    /// <paramref name="chosenCompId"/> becomes the choice. Switching to Auto drops the manual choice so the advisor
    /// picks again (the stored id is kept in the settings for a switch back).
    /// </summary>
    public void SetCompMode(CompMode mode, string? chosenCompId)
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
        _parser?.AbandonCurrentGame();
        _parser = new PowerParser(DateOnly.FromDateTime(sessionStart));
        _parser.AddListener(_constructed);
        _parser.AddListener(_battlegrounds);
        _parser.AddListener(this);
        _scene = "UNKNOWN";
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
                        if (DateTime.UtcNow >= _nextParseErrorNote)
                        {
                            _nextParseErrorNote = DateTime.UtcNow.AddMinutes(1);
                            Diag.Error($"Skipped a Power.log line the parser could not read: {line[..Math.Min(line.Length, 200)]}", ex);
                        }
                    }
                }

                _parser?.Flush();
                AfterPowerBatch();
                break;
            case "LoadingScreen.log":
                foreach (var line in lines) HandleLoadingScreen(line);
                break;
            case "Decks.log":
                foreach (var line in lines) HandleDecks(line);
                break;
        }

        _catchingUp = false;
        _dirty = true;
    }

    public void OnTick()
    {
        lock (_pipelineGate)
        {
            OnTickCore();
        }
    }

    private void OnTickCore()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextProcessCheck)
        {
            _nextProcessCheck = now.AddSeconds(2);
            var running = HearthstoneInstall.IsRunning();
            if (running != _hearthstoneRunning)
            {
                _hearthstoneRunning = running;
                _dirty = true;
            }
        }

        if (now >= _nextConfigCheck)
        {
            _nextConfigCheck = now.AddSeconds(30);
            _logConfigMissing = LogConfig.MissingSections(InstallDirectory);
            if (ClientConfig.LimitActive(InstallDirectory))
            {
                _logConfigMissing = [.. _logConfigMissing, "log size limit"];
                if (now >= _nextClientConfigFix && ClientConfig.LogSizeLimit(InstallDirectory) is not -1)
                {
                    // Removed again (a game repair, another tool): put it back so the next client start has no limit.
                    _nextClientConfigFix = now.AddMinutes(10);
                    LogConfig.EnsureAll(InstallDirectory);
                }
            }
        }

        // The memory is read on its own thread; this thread only says what it needs and applies what came back.
        _memoryPoller.Post(_hearthstoneRunning ? BuildMemoryRequest() : null);
        if (_memoryPoller.Take() is { } reading)
        {
            ApplyMemory(reading);
        }

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
                  + $"{(g.IsComplete ? "over" : "live")}, {g.Entities.Count} entities";
            Diag.Debug($"Tracker: scene {_scene}, Hearthstone {(_hearthstoneRunning ? "running" : "not running")}, game {game}, "
                       + $"{_publishCount} updates (slowest {_slowestPublishMs:N0} ms), memory: {_memory.Status}");
            _publishCount = 0;
            _slowestPublishMs = 0;
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

    private void RecordIfFinished(GameState game, bool force)
    {
        if (_recorded || (!force && !_battlegrounds.LocalFinished))
        {
            return;
        }

        _recorded = true;
        var duos = game.IsDuos;
        int? before = _ratingAtGameStart is { } r ? (duos ? r.Duos : r.Solo) : null;
        var rank = _constructed.LocalRank?.Display;
        var races = _battlegrounds.MemoryRaces is { Count: > 0 } list ? string.Join(",", list) : null;
        var record = GameRecords.Build(game, _battlegrounds, _cards, _constructed.Deck, "live", before is > 0 ? before : null, null, rank, races);
        if (record is null)
        {
            return;
        }

        Diag.Info($"Game finished: {record.Mode}, {record.Result}, place {record.Placement}, {record.Turns} turns{(_catchingUp ? " (replayed)" : string.Empty)}");
        try
        {
            _history.Add(record);
        }
        catch (Exception ex)
        {
            Diag.Error("Saving game failed", ex);
        }

        if (_catchingUp)
        {
            return;
        }

        if (record.IsBattlegrounds)
        {
            _sessionPlacements.Add(record.Placement);
            if (before is > 0)
            {
                _awaitingRatingKey = record.Key;
                _awaitingDuos = duos;
                _awaitingRatingUntil = DateTime.UtcNow.AddSeconds(120);
                if (_hearthstoneRunning)
                {
                    _memoryPoller.Post(BuildMemoryRequest());
                    _memoryPoller.Wake();
                }
            }
        }

        if (record.Result == "Win") _sessionWins++;
        else if (record.Result == "Loss") _sessionLosses++;

        _lastOutcome = new GameOutcome(record.Mode, record.Result, record.Placement, record.RatingBefore, null, record.HeroCardId);
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
            _awaitingRatingKey);
    }

    /// <summary>Applies a memory reading on the tracking thread. Values read for an earlier game are dropped.</summary>
    private void ApplyMemory(MemoryReading reading)
    {
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
        else if (DateTime.UtcNow > _awaitingRatingUntil)
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
        var separator = line.IndexOf(" - ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }

        var content = line[(separator + 3)..].Trim();
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
                var rating = _rating is null ? (int?)null : game.IsDuos ? _rating.Duos : _rating.Solo;
                battlegrounds = Advisor.Enrich(battlegrounds, rating, _cards);
            }
            else if (inGame && game!.IsConstructedLike && game.LocalPlayerId != 0)
            {
                constructed = _constructed.BuildView(game, _cards);
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
            _logConfigMissing);

        SnapshotChanged?.Invoke(Current);
    }

    [GeneratedRegex(@"LoadingScreen\.OnSceneLoaded\(\) - prevMode=\w+ currMode=(?<mode>\w+)")]
    private static partial Regex SceneRegex();
}
