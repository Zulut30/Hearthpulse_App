using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>The class a player plays: a hero card without a class (Deathwing, Worldbreaker) does not change it.</summary>
internal static class HeroClass
{
    /// <summary>
    /// The class of the player's hero; when that hero has none (a neutral hero card replaced it), the class of the
    /// player's first hero that has one - the hero they started with.
    /// </summary>
    public static string Of(GameState game, Entity? player, CardDb cards)
    {
        var current = cards.Get(game.HeroOf(player)?.CardId)?.CardClass;
        if (IsClass(current) || player is null)
        {
            return current ?? string.Empty;
        }

        var controller = player.GetInt(GameTag.Controller);
        Entity? first = null;
        foreach (var entity in game.Entities.Values)
        {
            if (entity.IsHero && entity.Controller == controller && (first is null || entity.Id < first.Id) && IsClass(cards.Get(entity.CardId)?.CardClass))
            {
                first = entity;
            }
        }

        return first is null ? current ?? string.Empty : cards.Get(first.CardId)!.CardClass;
    }

    private static bool IsClass(string? cardClass) => cardClass is { Length: > 0 } && cardClass != "NEUTRAL";
}
