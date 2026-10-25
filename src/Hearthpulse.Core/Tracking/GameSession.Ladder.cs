using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Core.Tracking;

/// <summary>The ranked ladder session (Standard and Wild) for the overlay widget and the Overview page.</summary>
public sealed partial class GameSession
{
    private DateTime? _logSessionStart;
    private TimeSpan _ladderBreak = LadderSession.DefaultBreak;
    private IReadOnlyList<GameRecord> _ladderGames = [];
    private volatile bool _ladderReload = true;

    /// <summary>A break longer than this with no game starts a new ladder session; the host passes the setting.</summary>
    public void SetLadderBreak(TimeSpan breakAfter)
    {
        if (_ladderBreak == breakAfter)
        {
            return;
        }

        _ladderBreak = breakAfter;
        _dirty = true;
    }

    private void OnLadderLogSession(DateTime sessionStart)
    {
        _logSessionStart = sessionStart;
        _ladderReload = true;
    }

    /// <summary>The session's ranked games read again only after the history changed; the rank of the game being played is live.</summary>
    private LadderSessions BuildLadder(Power.GameState? game, bool inGame)
    {
        var now = Clock().ToLocalTime();
        if (_ladderReload)
        {
            _ladderReload = false;
            try
            {
                _ladderGames = _history.Since(_logSessionStart ?? now - TimeSpan.FromDays(1));
            }
            catch (Exception ex)
            {
                Diag.Error("Reading the ladder session failed", ex);
            }
        }

        var ranked = inGame && game is { GameType: "GT_RANKED" };
        return LadderSession.Build(_ladderGames, now, _logSessionStart, _ladderBreak, ranked ? game!.FormatType : null, ranked ? _constructed.LocalRank : null);
    }
}
