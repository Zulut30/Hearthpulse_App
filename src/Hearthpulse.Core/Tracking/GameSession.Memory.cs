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
                    .Order(StringComparer.Ordinal)
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
}
