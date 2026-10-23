using System.Globalization;
using System.Text.Json;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Power;

namespace Hearthlens.Core.Tracking;

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
        string? races = null)
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
        };
    }
}
