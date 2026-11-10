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
    /// stand in tier D to choose from. The first archetype (the one chosen by itself) has a full side card: twelve
    /// cards of a mulligan guide and five lists; the others have neither.
    /// </summary>
    public static ConstructedMeta States()
    {
        static MetaArchetype Archetype(int id, string name, string heroClass, int games, double winRate, double share, bool split) =>
            new(id, name, name, heroClass, games, (int)(games * winRate), winRate, share,
                split ? games / 2 : 0, winRate + 0.02, split ? games / 2 : 0, winRate - 0.02, [], []);

        string[] kept =
        [
            "CS2_065", "EX1_319", "CS2_188", "EX1_029", "CS2_189", "EX1_008", "CS2_172", "EX1_015", "CS2_120", "EX1_066", "CS2_122", "EX1_506",
        ];
        MetaArchetype[] archetypes =
        [
            Archetype(1, "Zoolock", "WARLOCK", 31000, 0.561, 0.189, true) with
            {
                Mulligan = kept.Select((id, i) => new MetaMulliganCard(id, 9000 - (i * 500), 0.95 - (i * 0.06), 7000 - (i * 400), 0.60 - (i * 0.012))).ToList(),
            },
            Archetype(2, "Handbuff Paladin of the Long Name", "PALADIN", 12400, 0.542, 0.081, true),
            Archetype(3, "Control Warrior", "WARRIOR", 9000, 0.501, 0.060, true),
            Archetype(4, "Thief Priest", "PRIEST", 5200, 0.493, 0.034, false),
            Archetype(5, "Big Rogue", "ROGUE", 4000, 0.481, 0.031, true),
            Archetype(6, "Quest Mage", "MAGE", 2100, 0.452, 0.012, true),
            Archetype(7, "Token Druid", "DRUID", 1300, 0.431, 0.008, false),
        ];
        var seen = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var lists = Enumerable.Range(0, 5)
            .Select(i => new MetaDeck("AAECAf0GAA==", 1, "Zoolock", "WARLOCK", 4000 - (i * 700), 2300 - (i * 420), 0.58 - (i * 0.01), seen))
            .ToList();
        return new ConstructedMeta("standard", "legend", "past_week", new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc), 65000, archetypes, lists);
    }
}
