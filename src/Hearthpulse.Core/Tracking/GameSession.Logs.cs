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

public sealed partial class GameSession
{
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
        else if (!_recorded && _parser?.Game is { IsBattlegrounds: true } over && _battlegrounds.LocalFinished)
        {
            // The final place may never come (already right before the end, or the log went quiet): the wait runs out here.
            RecordWhenPlaceKnown(over);
            _dirty |= _recorded;
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
            _ = long.TryParse(content.AsSpan(11), CultureInfo.InvariantCulture, out _pendingDeckId);
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
