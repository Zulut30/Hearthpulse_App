using System.Text;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;

namespace Hearthlens.Tests.Sim;

/// <summary>
/// Board capture from Power.log lines (synthetic, in the format of the real log): when it happens and what the
/// simulator is given. The start-of-combat structure is inferred and not yet checked against a real log.
/// </summary>
public sealed class CaptureTests
{
    private const int Me = 5;
    private const int Them = 13;

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [
          {"id":"BG36_207","dbfId":1001,"name":"Wolf Pup","type":"MINION","attack":3,"health":6,"races":["BEAST"],"mechanics":["BACON_RALLY"],"techLevel":3,"isBattlegroundsPoolMinion":true,"battlegroundsPremiumDbfId":1002,"text":"<b>Rally:</b> Give your other minions +4/+1."},
          {"id":"BG36_207_G","dbfId":1002,"name":"Wolf Pup","type":"MINION","attack":6,"health":12,"races":["BEAST"],"mechanics":["BACON_RALLY"],"techLevel":3,"battlegroundsNormalDbfId":1001,"text":"<b>Rally:</b> Give your other minions +8/+2."},
          {"id":"BG36_854","dbfId":1003,"name":"Rescue Bot","type":"MINION","attack":2,"health":1,"races":["MECHANICAL"],"mechanics":["DEATHRATTLE","TAUNT"],"techLevel":2,"isBattlegroundsPoolMinion":true,"text":"<b>Taunt</b> <b>Deathrattle:</b> Get a Repair Job."},
          {"id":"BG32_236","dbfId":1004,"name":"Aureate Laureate","type":"MINION","attack":2,"health":2,"races":["PIRATE"],"mechanics":["DIVINE_SHIELD"],"techLevel":1,"isBattlegroundsPoolMinion":true,"text":"<b>Divine Shield</b>"},
          {"id":"BG25_001","dbfId":1005,"name":"Risen Rider","type":"MINION","attack":2,"health":1,"races":["UNDEAD"],"mechanics":["REBORN","TAUNT"],"techLevel":1,"isBattlegroundsPoolMinion":true,"text":"<b>Taunt</b> <b>Reborn</b>"}
        ]
        """)));

    [Fact]
    public void Start_of_combat_attacks_do_not_capture_the_board()
    {
        var log = new PowerLog()
            .BattlegroundsGame()
            .Entity(20, "TB_BaconShop_HP_069", Me, "PLAY", "HERO_POWER")
            .Minion(30, "BG25_001", Me, 1, 4, 4)
            .Minion(40, "BG36_854", Them, 1, 2, 1)
            .Minion(41, "BG32_236", Them, 2, 3, 3)
            .CombatStarts()
            // Illidan's Wingmen: our minion attacks immediately, inside the hero power's TRIGGER block.
            .Block("TRIGGER", Ref(20, "Wingmen", "TB_BaconShop_HP_069", Me))
            .Block("ATTACK", Ref(30, "Risen Rider", "BG25_001", Me))
            .Tag(Ref(40, "Rescue Bot", "BG36_854", Them), "DAMAGE", "4")
            .Tag(Ref(30, "Risen Rider", "BG25_001", Me), "DAMAGE", "2")
            .Tag(Ref(40, "Rescue Bot", "BG36_854", Them), "ZONE", "GRAVEYARD")
            .End()
            .End()
            // The first regular attack: theirs.
            .Block("ATTACK", Ref(41, "Aureate Laureate", "BG32_236", Them));

        var (_, tracker) = log.Replay(Cards);

        var capture = tracker.LastCapture!;
        Assert.False(capture.OursFirst);
        Assert.Equal(41, capture.FirstAttackerEntityId);
        var ours = Assert.Single(capture.Ours.Minions);
        Assert.Equal((4, 2), (ours.Attack, ours.Health));
        Assert.Equal("BG32_236", Assert.Single(capture.Theirs.Minions).CardId);
        Assert.Contains("TB_BaconShop_HP_069", capture.Ours.Extras);
        Assert.Equal("TB_BaconShop_HP_069", capture.Ours.HeroPowerCardId);
    }

    [Fact]
    public void Minions_removed_by_a_start_of_combat_trigger_are_remembered_by_its_source()
    {
        // A Stitched Salvager-style start-of-combat destroy: the minion to its left leaves the board inside its TRIGGER block.
        var log = new PowerLog()
            .BattlegroundsGame()
            .Minion(30, "BG25_001", Me, 1, 7, 3, ("TAUNT", "1"))
            .Minion(31, "BG31_999", Me, 2, 16, 4)
            .Minion(40, "BG36_854", Them, 1, 2, 1)
            .CombatStarts()
            .Block("TRIGGER", Ref(31, "Stitched Salvager", "BG31_999", Me))
            .Tag(Ref(30, "Risen Rider", "BG25_001", Me), "ZONE", "GRAVEYARD")
            .End()
            .Block("ATTACK", Ref(31, "Stitched Salvager", "BG31_999", Me));

        var (_, tracker) = log.Replay(Cards);

        var salvager = Assert.Single(tracker.LastCapture!.Ours.Minions);
        var copy = Assert.Single(salvager.Remembered);
        Assert.Equal(("BG25_001", 7, 3, true), (copy.CardId, copy.Attack, copy.Health, copy.Taunt));
    }

    [Fact]
    public void Attacks_inside_game_level_blocks_still_capture()
    {
        var log = new PowerLog()
            .BattlegroundsGame()
            .Minion(30, "BG25_001", Me, 1, 4, 4)
            .Minion(40, "BG36_854", Them, 1, 2, 1)
            .CombatStarts()
            .Block("TRIGGER", "GameEntity")
            .Block("ATTACK", Ref(40, "Rescue Bot", "BG36_854", Them));

        var (_, tracker) = log.Replay(Cards);

        var capture = tracker.LastCapture!;
        Assert.False(capture.OursFirst);
        Assert.Equal(40, capture.FirstAttackerEntityId);
        Assert.Single(capture.Ours.Minions);
    }

    [Fact]
    public void Captured_minions_carry_card_data_and_only_unsimulated_effects_count()
    {
        var log = new PowerLog()
            .BattlegroundsGame()
            // Golden Wolf Pup (Rally; no effect in the empty registry used below), buffed.
            .Minion(30, "BG36_207_G", Me, 1, 9, 14, ("PREMIUM", "1"), ("CARDRACE", "BEAST"))
            // Rescue Bot: its deathrattle only gives a card, so it is not "not simulated" any more.
            .Minion(31, "BG36_854", Me, 2, 2, 1, ("TAUNT", "1"), ("DEATHRATTLE", "1"), ("CARDRACE", "MECHANICAL"))
            // Aureate Laureate with a deathrattle granted by an enchantment.
            .Minion(40, "BG32_236", Them, 1, 5, 5, ("DEATHRATTLE", "1"), ("DIVINE_SHIELD", "1"), ("TAUNT", "1"))
            .Entity(50, "BG_TEST_DR_e", Them, "PLAY", "ENCHANTMENT", ("ATTACHED", "40"))
            // A minion in our hand (ours is visible to us; theirs never is).
            .Entity(60, "BG25_001", Me, "HAND", "MINION", ("ATK", "2"), ("HEALTH", "1"))
            .CombatStarts()
            .Block("ATTACK", Ref(30, "Wolf Pup", "BG36_207_G", Me));

        // No registered effects, so the outcome does not depend on which cards Hearthlens.Core implements.
        var (_, tracker) = log.Replay(Cards, EffectRegistry.Empty);
        var capture = tracker.LastCapture!;

        var pup = capture.Ours.Minions[0];
        Assert.Equal("BG36_207", pup.CardId);
        Assert.True(pup.Golden);
        Assert.Equal(Tribe.Beast, pup.Tribes);
        Assert.Equal(6, pup.RebornAttack);
        Assert.Equal(CardMechanic.Rally, pup.Mechanics & CardMechanic.Rally);
        Assert.True(pup.HasUnsimulatedEffect);

        var bot = capture.Ours.Minions[1];
        Assert.False(bot.HasUnsimulatedEffect);
        Assert.Equal(Keyword.Taunt, bot.BaseKeywords);
        Assert.Equal(Tribe.Mech, bot.Tribes);

        var laureate = Assert.Single(capture.Theirs.Minions);
        Assert.Equal(["BG_TEST_DR_e"], laureate.Enchantments);
        Assert.True(laureate.HasUnsimulatedEffect);
        // Reborn would bring back the printed card: Divine Shield, not the Taunt it was given.
        Assert.Equal(Keyword.DivineShield, laureate.BaseKeywords);

        Assert.Equal("BG25_001", Assert.Single(capture.Ours.Hand).CardId);
        Assert.Empty(capture.Theirs.Hand);
        Assert.Equal(2, tracker.BuildView(log.State!).CombatOdds!.Unsimulated);
    }

    [Fact]
    public void Registered_effects_turn_the_flag_off()
    {
        var log = new PowerLog()
            .BattlegroundsGame()
            .Minion(30, "BG36_207", Me, 1, 3, 6)
            .Minion(40, "BG32_236", Them, 1, 5, 5, ("DEATHRATTLE", "1"))
            .Entity(50, "BG_TEST_DR_e", Them, "PLAY", "ENCHANTMENT", ("ATTACHED", "40"))
            .CombatStarts()
            .Block("ATTACK", Ref(30, "Wolf Pup", "BG36_207", Me));

        var (game, tracker) = log.Replay(Cards, EffectRegistry.FromTypes(typeof(WolfPupStub), typeof(GrantedDeathrattleStub)));

        Assert.All(tracker.LastCapture!.Ours.Minions.Concat(tracker.LastCapture.Theirs.Minions), m => Assert.False(m.HasUnsimulatedEffect));
        Assert.Equal(0, tracker.BuildView(game).CombatOdds!.Unsimulated);
    }

    private static string Ref(int id, string name, string cardId, int player) =>
        $"[entityName={name} id={id} zone=PLAY zonePos=1 cardId={cardId} player={player}]";

    [CardEffect("BG36_207")]
    private sealed class WolfPupStub : CombatEffect
    {
        public override void OnAttack(Fight fight, Unit self, Unit target)
        {
        }
    }

    [CardEffect("BG_TEST_DR_e")]
    private sealed class GrantedDeathrattleStub : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self)
        {
        }
    }

    /// <summary>Builds GameState lines of a Battlegrounds Power.log.</summary>
    private sealed class PowerLog
    {
        private readonly List<string> _lines = new();

        public GameState? State { get; private set; }

        public PowerLog Power(string body)
        {
            _lines.Add("D 21:15:00.0000000 GameState.DebugPrintPower() - " + body);
            return this;
        }

        public PowerLog BattlegroundsGame() => Power("CREATE_GAME")
            .Power("    GameEntity EntityID=1")
            .Power("        tag=CARDTYPE value=GAME")
            .Power($"    Player EntityID=2 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]")
            .Power($"        tag=CONTROLLER value={Me}")
            .Power("        tag=CARDTYPE value=PLAYER")
            .Power("        tag=BACON_BLOODGEMBUFFATKVALUE value=3")
            .Power($"    Player EntityID=3 PlayerID={Them} GameAccountId=[hi=0 lo=0]")
            .Power($"        tag=CONTROLLER value={Them}")
            .Power("        tag=CARDTYPE value=PLAYER")
            .Power("        tag=BACON_DUMMY_PLAYER value=1")
            .GameLine("GameType=GT_BATTLEGROUNDS")
            .GameLine($"PlayerID={Me}, PlayerName=Me#1234")
            .GameLine($"PlayerID={Them}, PlayerName=Bob's Opponent");

        public PowerLog GameLine(string body)
        {
            _lines.Add("D 21:15:00.0000000 GameState.DebugPrintGame() - " + body);
            return this;
        }

        public PowerLog Entity(int id, string cardId, int controller, string zone, string cardType, params (string Tag, string Value)[] tags)
        {
            Power($"FULL_ENTITY - Creating ID={id} CardID={cardId}");
            Power($"    tag=CONTROLLER value={controller}");
            Power($"    tag=CARDTYPE value={cardType}");
            Power($"    tag=ZONE value={zone}");
            foreach (var (tag, value) in tags)
            {
                Power($"    tag={tag} value={value}");
            }

            return this;
        }

        public PowerLog Minion(int id, string cardId, int controller, int position, int attack, int health, params (string Tag, string Value)[] tags) =>
            Entity(id, cardId, controller, "PLAY", "MINION",
                   [("ZONE_POSITION", position.ToString()), ("ATK", attack.ToString()), ("HEALTH", health.ToString()), .. tags]);

        public PowerLog CombatStarts() => Power("TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=2 ");

        public PowerLog Block(string type, string entity) =>
            Power($"BLOCK_START BlockType={type} Entity={entity} EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ");

        public PowerLog Tag(string entity, string tag, string value) => Power($"TAG_CHANGE Entity={entity} tag={tag} value={value} ");

        public PowerLog End() => Power("BLOCK_END");

        public (GameState Game, BattlegroundsTracker Tracker) Replay(CardDb cards, EffectRegistry? effects = null)
        {
            var parser = new PowerParser(new DateOnly(2026, 10, 21));
            var tracker = new BattlegroundsTracker { Cards = cards, Effects = effects ?? EffectRegistry.Default };
            parser.AddListener(tracker);
            foreach (var line in _lines)
            {
                parser.Feed(line);
            }

            parser.Flush();
            State = parser.Game!;
            Assert.Equal(Me, State.LocalPlayerId);
            return (State, tracker);
        }
    }
}
