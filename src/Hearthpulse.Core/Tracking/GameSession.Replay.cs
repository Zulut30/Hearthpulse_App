using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.Tracking;

/// <summary>The match replay of each finished game (<c>docs/match-replay.md</c>).</summary>
public sealed partial class GameSession
{
    /// <summary>Where replays are saved; tests point it at a scratch folder.</summary>
    internal ReplayStore Replays { get; set; } = ReplayStore.Default;

    private GameRecord SaveReplay(GameRecord record, GameState game)
    {
        try
        {
            // Live recordings replace an earlier file of the same game key: the part kept when the client left a game that
            // then went on in the same log. A reconnect has another key; HistoryStore hands the left part's file over.
            return ReplayCapture.Attach(record, game, _replay, _battlegrounds, Replays,
                _constructed.LocalRank?.Text, _constructed.OpponentRank?.Text, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Error("Saving the match replay failed", ex);
            return record;
        }
    }
}
