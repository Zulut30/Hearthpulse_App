using Hearthpulse.Core.Battlegrounds;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// The combat odds panel for render checks (<c>--render-ui</c>): a made-up close fight on turn 9, simulated for real,
/// where the player has 7 health left (so the fight can kill them) and the opponent 10.
/// </summary>
public static class CombatOddsScene
{
    public static BattlegroundsView Synthetic()
    {
        static SimMinion M(int attack, int health, int tier, Keyword keywords = Keyword.None) =>
            SimMinion.Create(string.Empty, attack, health, keywords: keywords) with { TechLevel = tier };

        var ours = new SimSide([M(6, 7, 3), M(4, 9, 4, Keyword.Taunt), M(8, 3, 5), M(3, 3, 2, Keyword.DivineShield)]) { TavernTier = 4, Health = 7 };
        var theirs = new SimSide([M(7, 6, 4), M(5, 8, 5, Keyword.Taunt), M(4, 4, 3), M(2, 6, 2, Keyword.DivineShield), M(1, 1, 1)])
        {
            TavernTier = 5,
            Health = 10,
        };
        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 2000, DamageCap = 15 });
        return new BattlegroundsView(9, 4, 6, "TB_BaconShop_HERO_72", 5, 2, 6, false, true, false, [], [], false, [], [], [], [], null, 0, [], [])
        {
            CombatOdds = odds,
        };
    }
}
