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
    /// <summary>
    /// Saves a Battlegrounds game that is over for us. The client writes our final place a few milliseconds after our
    /// PLAYSTATE, so a log poll can fall between them: the game waits for the place, at most <see cref="FinalPlaceWait"/>.
    /// </summary>
    private void RecordWhenPlaceKnown(GameState game)
    {
        _localFinishedAt ??= Clock();
        if (_battlegrounds.FinalPlaceKnown || Clock() - _localFinishedAt >= FinalPlaceWait)
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
        var races = _battlegrounds.MemoryRaces is { Count: > 0 } list ? string.Join(',', list) : null;
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
}
