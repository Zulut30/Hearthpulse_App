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
            var header = new ReplayHeader { Summary = Summary(record, recorder.IsBattlegrounds ? recorder.Battlegrounds : null, battlegrounds) };
            text = recorder.IsBattlegrounds && recorder.HasGame
                ? recorder.Battlegrounds.Text(header, battlegrounds.ReplayData(game))
                : ReplayFile.WriteBattlegrounds(header, battlegrounds.ReplayData(game));
        }
        else if (recorder.HasGame)
        {
            // What the log revealed by the end is only for a game that ended: an abandoned one may still be going on.
            text = recorder.Text(new ReplayHeader { LocalRank = localRank, OpponentRank = opponentRank, Summary = Summary(record, null, null) },
                postGame: !record.IsAbandoned);
        }

        return text is not null && store.Save(record.Key, text, overwrite) is { } name ? record with { Replay = name } : record;
    }

    /// <summary>
    /// The game for the file's header, from its history row and, for Battlegrounds, the end of the recording: no names,
    /// no BattleTags, no deck name.
    /// </summary>
    internal static ReplaySummary Summary(GameRecord record, BattlegroundsReplayRecorder? steps, BattlegroundsTracker? battlegrounds) => new()
    {
        Mode = record.Mode,
        GameType = record.GameType,
        Format = record.Format,
        StartedAt = record.StartedAt.Kind == DateTimeKind.Utc ? record.StartedAt : record.StartedAt.ToUniversalTime(),
        Seconds = Math.Max(0, (int)record.Duration.TotalSeconds),
        HeroCardId = record.HeroCardId,
        PlayerClass = record.PlayerClass,
        OpponentHeroCardId = record.OpponentHeroCardId is { Length: > 0 } opponent ? opponent : null,
        OpponentClass = record.OpponentClass is { Length: > 0 } opponentClass ? opponentClass : null,
        Result = record.Result,
        Turns = record.Turns,
        WentFirst = record.WentFirst,
        DeckCode = record.DeckCode,
        OpponentArchetype = record.OpponentArchetype,
        Placement = record.Placement,
        RatingBefore = record.RatingBefore,
        RatingAfter = record.RatingAfter,
        TavernTier = record.TavernTier,
        AnomalyCardId = record.AnomalyCardId,
        Tribes = record.Races is { Length: > 0 } races ? races.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
        HeroPowers = steps?.HeroPowers ?? [],
        Trinkets = steps?.Trinkets ?? [],
        FinalBoard = battlegrounds?.LastOwnBoard.Select(m => new ReplayBgMinion(m.CardId, m.Attack, m.Health, m.Golden, m.Keywords)).ToList() ?? [],
        FinalHand = steps?.Hand ?? [],
    };
}
