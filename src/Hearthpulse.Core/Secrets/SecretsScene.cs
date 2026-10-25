using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Secrets;

/// <summary>
/// <c>--render-ui</c> scene of the Secrets panel: a Standard Mage with two Secrets, after a minion of yours attacked
/// their hero (Ice Barrier and Mystic Misdirection ruled out) and one Secret crossed out by hand. Built by running the
/// real tracker over synthetic Power.log lines, so the panel shows exactly what a game would give it.
/// </summary>
public static class SecretsScene
{
    public static ConstructedView? Synthetic(CardDb cards)
    {
        var tracker = new SecretTracker();
        var parser = new PowerParser(DateOnly.FromDateTime(DateTime.Today));
        parser.AddListener(tracker);
        foreach (var body in Lines())
        {
            parser.Feed("D 12:00:00.0000000 GameState." + (body.StartsWith("GameType=", StringComparison.Ordinal) || body.StartsWith("FormatType=", StringComparison.Ordinal)
                ? "DebugPrintGame() - "
                : "DebugPrintPower() - ") + body);
        }

        parser.Flush();
        if (parser.Game is not { } game)
        {
            return null;
        }

        tracker.CommitIfQuiet(game);
        tracker.Toggle("LOOT_101");
        var secrets = tracker.BuildView(game, cards);
        if (secrets is null)
        {
            return null;
        }

        return new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5,
            secrets.Slots.Count, null, null)
        {
            Secrets = secrets,
        };
    }

    private static IEnumerable<string> Lines()
    {
        static string Ref(int id, string zone, int player, string card = "") => $"[entityName=X id={id} zone={zone} zonePos=1 cardId={card} player={player}]";
        static string Tag(string entity, string tag, object value) => $"TAG_CHANGE Entity={entity} tag={tag} value={value} ";
        const string Effect = " EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

        string[] setup =
        [
            "CREATE_GAME",
            "GameEntity EntityID=1",
            "    tag=CARDTYPE value=GAME",
            "Player EntityID=2 PlayerID=1 GameAccountId=[hi=1 lo=1]",
            "    tag=CONTROLLER value=1",
            "    tag=CARDTYPE value=PLAYER",
            "    tag=HERO_ENTITY value=20",
            "Player EntityID=3 PlayerID=2 GameAccountId=[hi=1 lo=2]",
            "    tag=CONTROLLER value=2",
            "    tag=CARDTYPE value=PLAYER",
            "    tag=HERO_ENTITY value=30",
            "FULL_ENTITY - Creating ID=20 CardID=HERO_09",
            "    tag=ZONE value=PLAY",
            "    tag=CONTROLLER value=1",
            "    tag=CARDTYPE value=HERO",
            "FULL_ENTITY - Creating ID=30 CardID=HERO_08",
            "    tag=ZONE value=PLAY",
            "    tag=CONTROLLER value=2",
            "    tag=CARDTYPE value=HERO",
            "GameType=GT_RANKED",
            "FormatType=FT_STANDARD",
            "FULL_ENTITY - Creating ID=40 CardID=CS2_182",
            "    tag=ZONE value=HAND",
            "    tag=CONTROLLER value=1",
            "    tag=CARDTYPE value=MINION",
            "FULL_ENTITY - Creating ID=50 CardID=",
            "    tag=ZONE value=DECK",
            "    tag=CONTROLLER value=2",
            "FULL_ENTITY - Creating ID=51 CardID=",
            "    tag=ZONE value=DECK",
            "    tag=CONTROLLER value=2",
        ];
        foreach (var line in setup)
        {
            yield return line;
        }

        // Their turn 3: two Mage Secrets for 3 Mana each.
        yield return Tag("2", "CURRENT_PLAYER", 0);
        yield return Tag("3", "CURRENT_PLAYER", 1);
        yield return Tag("GameEntity", "TURN", 6);
        yield return Tag("GameEntity", "STEP", "MAIN_ACTION");
        var used = 0;
        foreach (var id in new[] { 50, 51 })
        {
            used += 3;
            yield return Tag(Ref(id, "DECK", 2), "ZONE", "HAND");
            yield return "BLOCK_START BlockType=PLAY Entity=" + Ref(id, "HAND", 2) + Effect;
            yield return Tag("3", "RESOURCES_USED", used);
            yield return Tag(Ref(id, "HAND", 2), "CLASS", "MAGE");
            yield return Tag(Ref(id, "HAND", 2), "SECRET", 1);
            yield return Tag(Ref(id, "HAND", 2), "ZONE", "SECRET");
            yield return "BLOCK_END";
        }

        // Your turn: a minion that was already on the board attacks their hero and nothing fires.
        yield return Tag("GameEntity", "STEP", "MAIN_NEXT");
        yield return Tag("3", "CURRENT_PLAYER", 0);
        yield return Tag("2", "CURRENT_PLAYER", 1);
        yield return Tag("GameEntity", "TURN", 7);
        yield return Tag("GameEntity", "STEP", "MAIN_READY");
        yield return Tag("GameEntity", "STEP", "MAIN_ACTION");
        yield return Tag(Ref(40, "HAND", 1, "CS2_182"), "ZONE", "PLAY");
        yield return "BLOCK_START BlockType=ATTACK Entity=" + Ref(40, "PLAY", 1, "CS2_182") + Effect;
        yield return Tag("GameEntity", "PROPOSED_ATTACKER", 40);
        yield return Tag("GameEntity", "PROPOSED_DEFENDER", 30);
        yield return Tag(Ref(30, "PLAY", 2, "HERO_08"), "DAMAGE", 4);
        yield return Tag("GameEntity", "PROPOSED_ATTACKER", 0);
        yield return Tag("GameEntity", "PROPOSED_DEFENDER", 0);
        yield return "BLOCK_END";
    }
}
