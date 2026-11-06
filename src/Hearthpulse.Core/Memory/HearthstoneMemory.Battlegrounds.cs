using System.Globalization;

namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    /// <summary>Rating shown on the post-game screen once the server has sent it, else null.</summary>
    public int? GetPostGameBattlegroundsRating() => Safe<int?>(rt =>
    {
        var gameEntity = GameStateInstance(rt)["m_gameEntity"];
        var changeField = Field(gameEntity.Class, "ratingChange", ["<RatingChangeData>k__BackingField"]);
        var change = changeField is null ? default : gameEntity.Object(changeField);
        if (change.IsNull)
        {
            return null;
        }

        var newRatingField = Field(change.Class, "newRating", ["_NewRating"], integer: true);
        var value = newRatingField is null ? -1 : change.Int(newRatingField, -1);
        return IsPlausibleRating(value) ? value : null;
    });

    /// <summary>TAG_RACE values of the tribes in the current Battlegrounds lobby.</summary>
    public IReadOnlyList<int>? GetBattlegroundsRaces() => Safe(rt =>
    {
        var state = GameStateInstance(rt);
        var field = Field(state.Class, "races", ["m_availableRacesInBattlegroundsExcludingAmalgam"]);
        var list = field is null ? default : state.Object(field);
        if (list.IsNull)
        {
            return null;
        }

        var races = list.IntListItems();
        return races.Count is > 0 and < 16 ? races : null;
    });

    /// <summary>
    /// Live gold costs of the Battlegrounds game-mode buttons: freeze (slot 1), reroll (slot 2),
    /// tavern upgrade (slot 3) and Dark Gift (slot 5). Null when the button is not on screen.
    /// </summary>
    public BattlegroundsButtonCosts? GetBattlegroundsButtonCosts() => Safe<BattlegroundsButtonCosts?>(rt =>
    {
        var zoneMgr = Instance(rt, "ZoneMgr", "ZoneMgr");
        var zonesField = Field(zoneMgr.Class, "zones", ["m_zones"]);
        if (zoneMgr.IsNull || zonesField is null)
        {
            return null;
        }

        int? freeze = null, roll = null, upgrade = null, darkGift = null;
        foreach (var zone in zoneMgr.Object(zonesField).ListItems())
        {
            var zoneClass = zone.Class;
            if (zoneClass?.Name != "ZoneGameModeButton")
            {
                continue;
            }

            var sideField = Field(zoneClass, "side", ["m_Side"], integer: true);
            var slotField = Field(zoneClass, "slot", ["m_ButtonSlot"], integer: true);
            var cardsField = Field(zoneClass, "cards", ["m_cards"]);
            if (sideField is null || slotField is null || cardsField is null || zone.Int(sideField) != 1)
            {
                continue;
            }

            var slot = zone.Int(slotField);
            if (slot is not (1 or 2 or 3 or 5))
            {
                continue;
            }

            foreach (var card in zone.Object(cardsField).ListItems())
            {
                var entityField = Field(card.Class, "entity", ["m_entity"]);
                var entity = entityField is null ? default : card.Object(entityField);
                if (entity.IsNull)
                {
                    continue;
                }

                var cardIdField = Field(entity.Class, "cardId", ["m_cardIdInternal"]);
                var cardId = cardIdField is null ? null : entity.String(cardIdField);
                if (cardId is null)
                {
                    continue;
                }

                // Slot 3 is the upgrade only when the card is the tier button; other slots are stable enough
                // to read by position (freeze, reroll, Dark Gift).
                if (slot == 3 && !cardId.StartsWith("TB_BaconShopTechUp", StringComparison.Ordinal))
                {
                    continue;
                }

                var costField = Field(entity.Class, "cost", ["m_realTimeCost"], integer: true);
                var cost = costField is null ? 0 : entity.Int(costField);
                switch (slot)
                {
                    case 1: freeze = cost; break;
                    case 2: roll = cost; break;
                    case 3: upgrade = cost; break;
                    case 5: darkGift = cost; break;
                }

                break;
            }
        }

        return new BattlegroundsButtonCosts(freeze, roll, upgrade, darkGift);
    });

    /// <summary>Gold cost of the live tavern-upgrade button, or null when it is not on screen.</summary>
    public int? GetTavernUpgradeCost() => GetBattlegroundsButtonCosts()?.Upgrade;

    /// <summary>
    /// Card id of the hero whose Battlegrounds leaderboard tile is under the mouse, or null. Read straight from the
    /// game's leaderboard so it works at any resolution or aspect ratio.
    /// </summary>
    public string? GetHoveredLeaderboardHero() => Safe(rt =>
    {
        var manager = Instance(rt, "PlayerLeaderboardManager", "PlayerLeaderboardManager");
        var hovered = Field(manager.Class, "leaderboardHovered", ["m_isMousedOver"]);
        if (manager.IsNull || hovered is null || !manager.Bool(hovered))
        {
            return null;
        }

        var tileField = Field(manager.Class, "hoveredTile", ["m_currentlyMousedOverTile"]);
        var tile = tileField is null ? default : manager.Object(tileField);
        if (tile.IsNull)
        {
            return null;
        }

        // A tile whose hero entity is not set yet still carries the hero in its plain entity.
        var heroField = Field(tile.Class, "heroEntity", ["m_playerHeroEntity"]);
        var hero = heroField is null ? default : tile.Object(heroField);
        if (hero.IsNull)
        {
            var entityField = Field(tile.Class, "entity", ["m_entity"]);
            hero = entityField is null ? default : tile.Object(entityField);
        }

        var cardIdField = Field(hero.Class, "cardId", ["m_cardIdInternal"]);
        return hero.IsNull || cardIdField is null ? null : hero.String(cardIdField);
    });
}
