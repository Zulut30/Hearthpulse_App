using Hearthpulse.Core.Data;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Tracking;

/// <summary>The OBS window "Session" (proposal B1): rank or rating since the stream started, built only while it is on.</summary>
public sealed partial class GameSession
{
    private bool _streamSessionOn;
    private DateTime? _streamSessionReset;
    private IReadOnlyList<GameRecord> _streamGames = [];
    private DateTime? _streamGamesFrom;

    /// <summary>The window is on, and the player's "Start a new session" time (null: the ladder session's rule).</summary>
    public void SetStreamSession(bool on, DateTime? reset)
    {
        if (_streamSessionOn == on && _streamSessionReset == reset)
        {
            return;
        }

        _streamSessionOn = on;
        _streamSessionReset = reset;
        _streamGamesFrom = null;
        _dirty = true;
    }

    /// <summary>
    /// The window's view, or null while it is off. Runs before <see cref="BuildLadder"/>, so a history change it is about
    /// to reload is seen here too. The games are read again only after the history changed or the start moved earlier.
    /// </summary>
    private StreamSessionView? BuildStreamSession(Power.GameState? game, bool inGame)
    {
        if (!_streamSessionOn)
        {
            return null;
        }

        var now = Clock().ToLocalTime();
        var from = new[] { _streamSessionReset, _logSessionStart, now - TimeSpan.FromDays(1) }.OfType<DateTime>().Min();
        if (_ladderReload || _streamGamesFrom is null || from < _streamGamesFrom)
        {
            try
            {
                _streamGames = _history.Since(from);
                _streamGamesFrom = from;
            }
            catch (Exception ex)
            {
                Diag.Error("Reading the stream session failed", ex);
            }
        }

        var since = StreamSession.Since(_streamSessionReset, LadderSession.SessionStart(_streamGames, now, _logSessionStart, _ladderBreak), now);
        var ranked = inGame && game is { GameType: "GT_RANKED" };
        var mode = inGame && game?.GameType is { } type ? GameRecords.ModeOf(type) : null;
        var bgMode = mode is "Battlegrounds" or "Duos" ? mode : null;
        return StreamSession.Build(
            _streamGames,
            since,
            ranked ? game!.FormatType : null,
            ranked ? _constructed.LocalRank : null,
            bgMode,
            bgMode switch
            {
                "Battlegrounds" => _rating?.Solo is > 0 and var solo ? solo : null,
                "Duos" => _rating?.Duos is > 0 and var duos ? duos : null,
                _ => null,
            });
    }
}
