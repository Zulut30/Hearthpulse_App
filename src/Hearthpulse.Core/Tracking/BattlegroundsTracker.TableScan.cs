using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

public sealed partial class BattlegroundsTracker
{
    /// <summary>
    /// The entities <see cref="BuildView"/> reads, sorted out in one pass over the game. A Battlegrounds game holds
    /// about 2 000 entities; the view used to walk them eight times with LINQ and asked for the opponent's player id
    /// (a loop over the players) once per entity, which was most of a snapshot's cost on the log thread
    /// (bench <c>replay</c>, 2026-11-07). Each list keeps the order of <see cref="GameState.Entities"/>, so the stable
    /// sorts that follow give the rows they gave before.
    /// </summary>
    private sealed class TableScan
    {
        public List<Entity> LocalHand { get; } = [];

        public List<Entity> LocalPlay { get; } = [];

        /// <summary>The local player's minions in play, in table order.</summary>
        public List<Entity> LocalMinions { get; private set; } = [];

        /// <summary>Bob's offer (<see cref="ShopCards"/>), in row order.</summary>
        public List<Entity> Shop { get; private set; } = [];

        public List<Entity> Heroes { get; } = [];

        /// <summary>Enchantments in play attached to the local player entity (Battlegrounds counters).</summary>
        public List<Entity> PlayerEnchantments { get; } = [];

        public static TableScan Of(GameState game)
        {
            var scan = new TableScan();
            var local = game.LocalPlayerId;
            var opponent = game.OpponentPlayerId;
            var playerEntity = game.LocalPlayerEntity?.Id ?? -1;
            foreach (var e in game.Entities.Values)
            {
                var controller = e.Controller;
                var zone = e.Zone;
                var type = e.CardType;
                var inPlay = zone == "PLAY";
                if (controller == local)
                {
                    if (zone == "HAND")
                    {
                        scan.LocalHand.Add(e);
                    }
                    else if (inPlay)
                    {
                        scan.LocalPlay.Add(e);
                        if (type == "MINION")
                        {
                            scan.LocalMinions.Add(e);
                        }
                    }
                }

                if (controller == opponent && inPlay && type is "MINION" or "SPELL" or "BATTLEGROUND_SPELL")
                {
                    scan.Shop.Add(e);
                }

                if (type == "HERO")
                {
                    scan.Heroes.Add(e);
                }
                else if (type == "ENCHANTMENT" && inPlay && e.GetInt("ATTACHED") == playerEntity)
                {
                    scan.PlayerEnchantments.Add(e);
                }
            }

            scan.LocalMinions = [.. scan.LocalMinions.OrderBy(e => e.ZonePosition)];
            scan.Shop = [.. scan.Shop.OrderBy(e => e.ZonePosition)];
            return scan;
        }
    }
}
