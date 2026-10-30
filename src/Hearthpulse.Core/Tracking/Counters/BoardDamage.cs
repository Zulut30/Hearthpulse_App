using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking.Counters;

/// <summary>
/// How much damage a player's board can deal to the enemy hero, from what both players see on the board: the attack
/// of every minion and of the hero (weapon and attack buffs included) that can still attack, times the attacks it has
/// left. For the player whose turn it is that is what is left this turn; for the other player it is what the board
/// can deal on their next turn, when every minion is ready again.
/// </summary>
/// <remarks>
/// The tags as real Power.logs of October 2026 set them. A minion played without Charge or Rush enters EXHAUSTED.
/// Its controller's turn start clears EXHAUSTED. An attack raises NUM_ATTACKS_THIS_TURN, and the last attack sets
/// EXHAUSTED; NUM_ATTACKS_THIS_TURN clears at the next turn start, EXHAUSTED only at the controller's own. A Rush
/// minion enters not EXHAUSTED: on that turn it can attack only minions. A temporary hero attack buff ends with the
/// turn (ATK back to the weapon's).
/// </remarks>
public static class BoardDamage
{
    /// <summary>The damage and its parts.</summary>
    /// <param name="ThisTurn">The player is the one taking the turn: the damage is what is left this turn.</param>
    /// <param name="Attackers">Card ids of the minions counted (one per minion) and of the weapon when the hero counts.</param>
    public sealed record Result(int Minions, int Hero, bool ThisTurn, IReadOnlyList<string> Attackers)
    {
        public int Total => Minions + Hero;
    }

    public static Result Of(GameState game, int playerId)
    {
        var player = game.Players.TryGetValue(playerId, out var slot) ? game.Entities.GetValueOrDefault(slot.EntityId) : null;
        var thisTurn = player?.GetInt("CURRENT_PLAYER") == 1;
        var minions = 0;
        var attackers = new List<string>();
        Entity? hero = null;
        Entity? weapon = null;
        // Every overlay update builds this for both players: one plain pass over the entities.
        foreach (var entity in game.Entities.Values)
        {
            if (!entity.InPlay || entity.Controller != playerId)
            {
                continue;
            }

            switch (entity.CardType)
            {
                case "MINION":
                    var damage = entity.Attack * MinionAttacks(entity, thisTurn, game.Turn);
                    if (damage > 0)
                    {
                        minions += damage;
                        attackers.Add(entity.CardId);
                    }

                    break;
                case "HERO":
                    hero = entity;
                    break;
                case "WEAPON":
                    weapon = entity;
                    break;
            }
        }

        var heroDamage = hero is null ? 0 : HeroDamage(hero, weapon, thisTurn);
        if (heroDamage > 0 && weapon is not null && weapon.CardId.Length > 0)
        {
            attackers.Add(weapon.CardId);
        }

        return new(minions, heroDamage, thisTurn, attackers);
    }

    /// <summary>Attacks a minion has against the enemy hero this turn (<paramref name="thisTurn"/>) or on its next turn.</summary>
    private static int MinionAttacks(Entity minion, bool thisTurn, int turn)
    {
        if (minion.Attack <= 0 || Held(minion) || minion.Has("DORMANT"))
        {
            return 0;
        }

        if (!thisTurn)
        {
            return Swings(minion);
        }

        // Summoned without Charge, or out of attacks; and a Rush minion's first turn is for minions only.
        if (minion.Has("EXHAUSTED") || (minion.EnteredPlayTurn == turn && !minion.Has("CHARGE")))
        {
            return 0;
        }

        return Math.Max(0, Swings(minion) - minion.GetInt("NUM_ATTACKS_THIS_TURN"));
    }

    /// <summary>The hero's damage: its attack (with the weapon) times the attacks left, or on the next turn the attack it keeps.</summary>
    private static int HeroDamage(Entity hero, Entity? weapon, bool thisTurn)
    {
        if (Held(hero))
        {
            return 0;
        }

        var swings = Math.Max(Swings(hero), weapon is null ? 1 : Swings(weapon));
        if (!thisTurn)
        {
            // Off its turn the hero keeps the weapon's attack and lasting buffs; this turn's buffs ended with the turn.
            return Math.Max(hero.Attack, weapon?.Attack ?? 0) * swings;
        }

        return hero.Has("EXHAUSTED") ? 0 : hero.Attack * Math.Max(0, swings - hero.GetInt("NUM_ATTACKS_THIS_TURN"));
    }

    /// <summary>Frozen or unable to attack: the character attacks neither this turn nor, frozen now, on its next one.</summary>
    private static bool Held(Entity character) => character.Has("FROZEN") || character.Has("CANT_ATTACK");

    private static int Swings(Entity character) => character.Has(GameTag.MegaWindfury) ? 4 : character.Has(GameTag.Windfury) ? 2 : 1;
}
