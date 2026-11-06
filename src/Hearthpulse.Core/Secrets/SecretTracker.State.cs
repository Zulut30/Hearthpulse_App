using System.Globalization;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Secrets;

public sealed partial class SecretTracker
{
    /// <summary>Starts tracking Secrets that entered the opponent's Secret zone (SECRET=1 can arrive after the zone).</summary>
    private void Refresh(GameState game)
    {
        var opponent = game.OpponentPlayerId;
        if (opponent == 0)
        {
            return;
        }

        foreach (var entity in game.Controlled(opponent, "SECRET"))
        {
            if (entity.Has(GameTag.Secret) && !_active.ContainsKey(entity.Id))
            {
                _active[entity.Id] = Register(game, entity);
            }
        }
    }

    /// <summary>Starts tracking a Secret the moment it is one (in their Secret zone with SECRET=1), so its turn is right.</summary>
    private void Track(GameState game, Entity entity)
    {
        if (IsOpponentSecret(game, entity) && !_active.ContainsKey(entity.Id))
        {
            _active[entity.Id] = Register(game, entity);
        }
    }

    private ActiveSecret Register(GameState game, Entity entity)
    {
        // Named only when the card that made it is itself face up: a hidden creator (CREATOR_DBID) is never used.
        var creator = game.Entities.GetValueOrDefault(entity.GetInt(GameTag.DisplayedCreator) is > 0 and var d ? d : entity.GetInt(GameTag.Creator));
        var visibleCreator = creator is { CardId.Length: > 0 } && creator.CardType is not ("PLAYER" or "GAME" or "")
                             && creator.Zone != "DECK" && !(creator.Controller == game.OpponentPlayerId && creator.Zone is "HAND" or "SECRET");
        return new ActiveSecret(entity.Id)
        {
            ClassTag = entity.GetString(GameTag.Class),
            Turn = GameState.PlayerTurnOf(game.Turn),
            KnownCardId = _seenInHand.GetValueOrDefault(entity.Id),
            FromDeck = entity.IsOriginalDeckCard,
            CreatorId = visibleCreator ? creator!.CardId : null,
        };
    }

    /// <summary>Active Secrets the player does not know (only those can be narrowed down).</summary>
    private IReadOnlyList<int> ActiveUnknown(GameState game)
    {
        Refresh(game);
        return _active.Values.Where(s => s.KnownCardId is null).Select(s => s.EntityId).ToList();
    }

    private static bool IsOpponentSecret(GameState game, Entity entity) =>
        entity.Controller == game.OpponentPlayerId && entity.Zone == "SECRET" && entity.Has(GameTag.Secret);

    private static Board BoardOf(GameState game)
    {
        int ownerMinions = 0, ownerHand = 0, localMinions = 0;
        var opponent = game.OpponentPlayerId;
        var local = game.LocalPlayerId;
        foreach (var e in game.Entities.Values)
        {
            if (e.Controller == opponent)
            {
                if (e.InPlay && e.IsMinion)
                {
                    ownerMinions++;
                }
                else if (e.InHand)
                {
                    ownerHand++;
                }
            }
            else if (e.Controller == local && e.InPlay && e.IsMinion)
            {
                localMinions++;
            }
        }

        return new Board(ownerMinions, ownerHand, localMinions);
    }

    private static bool IsLocalTurn(GameState game) => game.LocalPlayerEntity?.GetInt("CURRENT_PLAYER") == 1;

    private static bool IsLocalPlayer(GameState game, Entity entity) => entity.Id == game.LocalPlayerEntity?.Id;

    /// <summary>
    /// A minion the Secret can act on: Elusive minions (can't be targeted by spells) and Dormant ones may be skipped by
    /// a Secret, so their events never rule anything out.
    /// </summary>
    private static SecretNeeds TargetableIf(Entity minion) =>
        minion.Has("CANT_BE_TARGETED_BY_SPELLS") || minion.Has("DORMANT") || minion.Has("UNTOUCHABLE")
            ? SecretNeeds.None
            : SecretNeeds.Targetable;

    /// <summary>Mana crystals left this turn.</summary>
    private static int ManaLeft(Entity? player) =>
        player is null ? 0
        : player.GetInt("RESOURCES") + player.GetInt("TEMP_RESOURCES") - player.GetInt("RESOURCES_USED") - player.GetInt("OVERLOAD_LOCKED");

    /// <summary>Only plain Mana is judged: temporary crystals and Overload make "no Mana left" uncertain.</summary>
    private static bool ManaKnown(Entity player) => player.GetInt("TEMP_RESOURCES") == 0 && player.GetInt("OVERLOAD_LOCKED") == 0;

    private static int Int(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
