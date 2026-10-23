using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Replay;

/// <summary>Saves a finished game's replay next to its history row (live games and log imports alike).</summary>
public static class ReplayCapture
{
    /// <summary>
    /// Writes the replay of <paramref name="game"/> and returns the history row with its file name; the row unchanged
    /// when there is nothing to save (Mercenaries, a game the recorder never saw) or the save failed.
    /// </summary>
    public static GameRecord Attach(
        GameRecord record,
        GameState game,
        ReplayRecorder recorder,
        BattlegroundsTracker battlegrounds,
        ReplayStore store,
        string? localRank = null,
        string? opponentRank = null,
        bool overwrite = false)
    {
        string? text = null;
        if (game.IsBattlegrounds)
        {
            text = ReplayFile.WriteBattlegrounds(new ReplayHeader(), battlegrounds.ReplayData(game));
        }
        else if (recorder.HasGame)
        {
            text = recorder.Text(new ReplayHeader { LocalRank = localRank, OpponentRank = opponentRank });
        }

        return text is not null && store.Save(record.Key, text, overwrite) is { } name ? record with { Replay = name } : record;
    }
}
