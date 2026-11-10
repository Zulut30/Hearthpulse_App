namespace Hearthpulse.Core.Constructed;

/// <summary>
/// Made-up Standard stats for the UI render check (<c>--render-ui</c>, group <c>metatiers</c>): the states of the meta
/// tier list the data copy rarely has. Not real numbers, never shown by the app itself.
/// </summary>
public static class MetaTierScene
{
    /// <summary>
    /// Seven archetypes over tiers S, B, C and D: tier A has none (its head says so), five have the split by who went
    /// first (the "1st · coin" column) and two do not (a dash), one name is too long for its cell, and two archetypes
    /// stand in tier D to choose from.
    /// </summary>
    public static ConstructedMeta States()
    {
        static MetaArchetype Archetype(int id, string name, string heroClass, int games, double winRate, double share, bool split) =>
            new(id, name, name, heroClass, games, (int)(games * winRate), winRate, share,
                split ? games / 2 : 0, winRate + 0.02, split ? games / 2 : 0, winRate - 0.02, [], []);

        MetaArchetype[] archetypes =
        [
            Archetype(1, "Zoolock", "WARLOCK", 31000, 0.561, 0.189, true),
            Archetype(2, "Handbuff Paladin of the Long Name", "PALADIN", 12400, 0.542, 0.081, true),
            Archetype(3, "Control Warrior", "WARRIOR", 9000, 0.501, 0.060, true),
            Archetype(4, "Thief Priest", "PRIEST", 5200, 0.493, 0.034, false),
            Archetype(5, "Big Rogue", "ROGUE", 4000, 0.481, 0.031, true),
            Archetype(6, "Quest Mage", "MAGE", 2100, 0.452, 0.012, true),
            Archetype(7, "Token Druid", "DRUID", 1300, 0.431, 0.008, false),
        ];
        return new ConstructedMeta("standard", "legend", "past_week", new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), 65000, archetypes, []);
    }
}
