using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

public static class GameRecords
{
    public static string ModeOf(string gameType) => gameType switch
    {
        _ when gameType.Contains("BATTLEGROUNDS") && gameType.Contains("DUO") => "Duos",
        _ when gameType.Contains("BATTLEGROUNDS") => "Battlegrounds",
        _ when gameType.Contains("MERCENARIES") => "Mercenaries",
        "GT_RANKED" => "Ranked",
        "GT_CASUAL" => "Casual",
        _ when gameType.Contains("ARENA") => "Arena",
        "GT_TAVERNBRAWL" or "GT_FSG_BRAWL" or "GT_FSG_BRAWL_VS_FRIEND" or "GT_FSG_BRAWL_1P_VS_AI" or "GT_FSG_BRAWL_2P_COOP" => "Brawl",
        "GT_VS_AI" => "Practice",
        "GT_VS_FRIEND" => "Friendly",
        _ => "Other",
    };

    public static string KeyOf(GameState game) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{game.StartedAt:yyyyMMddHHmmss}|{game.LocalPlayer?.Name}|{game.GameType}");

    /// <summary>Builds a history row for a finished game, or null if it is not worth keeping.</summary>
    public static GameRecord? Build(
        GameState game,
        BattlegroundsTracker battlegrounds,
        CardDb cards,
        DeckDefinition? deck,
        string source,
        int? ratingBefore = null,
        int? ratingAfter = null,
        string? rank = null,
        string? races = null,
        GameFactsTracker? facts = null,
        RankInfo? rankInfo = null)
    {
        if (game.LocalPlayer is not { } local || game.GameType.Length == 0 || game.GameType == "GT_TUTORIAL")
        {
            return null;
        }

        // Games the client left before a result (crash, closed mid-game) have no trustworthy outcome.
        var playState = game.LocalPlayerEntity?.GetString(GameTag.PlayState);
        if (playState is not ("WON" or "LOST" or "TIED" or "CONCEDED"))
        {
            return null;
        }

        var mode = ModeOf(game.GameType);
        var hero = game.LocalHero;
        var ended = game.EndedAt ?? game.LastActivity;

        if (game.IsBattlegrounds)
        {
            if (hero is null || hero.CardId is "" or "TB_BaconShop_HERO_PH" || hero.InHand)
            {
                return null;
            }

            var placement = hero.GetInt(GameTag.PlayerLeaderboardPlace);
            var top = game.IsDuos ? 2 : 4;
            var board = battlegrounds.LastOwnBoard
                .Select(m => new { id = m.CardId, atk = m.Attack, hp = m.Health, gold = m.Golden })
                .ToList();
            return new GameRecord
            {
                Key = KeyOf(game),
                StartedAt = game.StartedAt,
                EndedAt = ended,
                Mode = mode,
                GameType = game.GameType,
                Format = game.FormatType,
                PlayerName = local.Name,
                HeroCardId = hero.CardId,
                PlayerClass = "NEUTRAL",
                Result = placement == 0 ? "Unknown" : placement <= top ? "Win" : "Loss",
                Placement = placement,
                RatingBefore = ratingBefore,
                RatingAfter = ratingAfter,
                Turns = game.BattlegroundsTurn,
                TavernTier = battlegrounds.FinalTechLevel,
                FinalBoard = board.Count > 0 ? JsonSerializer.Serialize(board) : null,
                Races = races,
                Source = source,
                CombatResults = CombatJson(battlegrounds.Combats),
                OfferedHeroes = facts is { OfferedHeroes.Count: > 0 } ? string.Join(",", facts.OfferedHeroes) : null,
                AnomalyCardId = game.GameEntity?.GetInt(GameTag.BaconAnomaly) is > 0 and var anomaly ? cards.Get(anomaly)?.Id : null,
            };
        }

        if (game.Turn < 1)
        {
            return null;
        }

        var opponentHero = game.OpponentHero?.CardId ?? string.Empty;
        return new GameRecord
        {
            Key = KeyOf(game),
            StartedAt = game.StartedAt,
            EndedAt = ended,
            Mode = mode,
            GameType = game.GameType,
            Format = game.FormatType,
            PlayerName = local.Name,
            OpponentName = game.OpponentPlayer?.Name ?? string.Empty,
            HeroCardId = hero?.CardId ?? string.Empty,
            OpponentHeroCardId = opponentHero,
            PlayerClass = cards.Get(hero?.CardId)?.CardClass ?? string.Empty,
            OpponentClass = cards.Get(opponentHero)?.CardClass ?? string.Empty,
            Result = playState switch
            {
                "WON" => "Win",
                "LOST" or "CONCEDED" => "Loss",
                "TIED" => "Tie",
                _ => "Unknown",
            },
            Turns = (game.Turn + 1) / 2,
            DeckName = deck?.Name,
            DeckCode = deck?.Code,
            Rank = rank,
            Source = source,
            RankStarLevel = rankInfo is { StarLevel: > 0 } ? rankInfo.StarLevel : null,
            RankLegend = rankInfo is { LegendRank: > 0 } ? rankInfo.LegendRank : null,
            WentFirst = WentFirst(game),
            DeckId = deck is { DeckId: > 0 } && DeckFits(deck, cards.Get(hero?.CardId)?.CardClass, cards) ? deck.DeckId : null,
        };
    }

    /// <summary>
    /// False when the deck's hero is of another class than the game's hero: the client did not announce a deck for this
    /// game (some modes, a reconnect) and the one queued for an earlier game is still held. Unknown classes pass.
    /// </summary>
    private static bool DeckFits(DeckDefinition deck, string? playerClass, CardDb cards)
    {
        var deckClass = deck.HeroDbfIds.Select(cards.Get).FirstOrDefault(c => c is not null)?.CardClass;
        return string.IsNullOrEmpty(deckClass) || string.IsNullOrEmpty(playerClass)
               || string.Equals(deckClass, playerClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the local player went first, false on the coin, null when the log did not say.</summary>
    private static bool? WentFirst(GameState game)
    {
        if (game.LocalPlayerEntity?.GetInt(GameTag.FirstPlayer) == 1)
        {
            return true;
        }

        return game.OpponentPlayerEntity?.GetInt(GameTag.FirstPlayer) == 1 ? false : null;
    }

    private static string? CombatJson(IReadOnlyList<CombatRecord> combats) =>
        combats.Count == 0
            ? null
            : JsonSerializer.Serialize(combats.Select(c => new
            {
                turn = c.Turn,
                result = c.Result,
                opponent = c.OpponentHeroCardId,
                taken = c.DamageTaken,
                dealt = c.DamageDealt,
            }));
}
