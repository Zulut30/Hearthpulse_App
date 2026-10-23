using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Battlegrounds counters. Confirmed ones read events and tags seen in real Power.logs (combat copies dying, minions
/// summoned, NUM_SPELLS_PLAYED_THIS_GAME, the reroll button's COST). Unconfirmed ones read tag names from the GameTag
/// enum and player enchantments from HearthstoneJSON that no real log has shown yet: these tests pin how they are read
/// and that nothing is shown before their data arrives.
/// </summary>
public sealed class BattlegroundsCountersTests
{
    private const int Me = 5;
    private const int Bob = 13;

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"TB_BaconShop_HERO_50","dbfId":59000,"name":"Tess","type":"HERO"},
          {"id":"BG25_008","dbfId":95261,"name":"Eternal Knight","type":"MINION","techLevel":2},
          {"id":"BG25_008_G","dbfId":95262,"name":"Eternal Knight","type":"MINION","techLevel":2,"battlegroundsNormalDbfId":95261},
          {"id":"BG_TTN_401","dbfId":108432,"name":"Ancestral Automaton","type":"MINION","techLevel":2},
          {"id":"BG31_924","dbfId":115563,"name":"Thaumaturgist","type":"MINION","techLevel":2},
          {"id":"BG23_017","dbfId":80755,"name":"Sanguine Champion","type":"MINION","techLevel":6},
          {"id":"BGFYM_000","dbfId":130610,"name":"C'Thun","type":"MINION","techLevel":1},
          {"id":"TB_BaconShop_8p_Reroll_Button","dbfId":57000,"name":"Refresh","type":"BATTLEGROUND_ACTION"}
        ]
        """u8.ToArray()));

    private static (GameState Game, BattlegroundsTracker Tracker) Play(params string[] bodies)
    {
        var (parser, tracker) = Start("GT_BATTLEGROUNDS", bodies);
        return (parser.Game!, tracker);
    }

    private static (PowerParser Parser, BattlegroundsTracker Tracker) Start(string gameType, params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker { Cards = Cards };
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=4",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=3",
            $"DebugPrintPower() -     Player EntityID=14 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]",
            $"DebugPrintPower() -         tag=CONTROLLER value={Me}",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            $"DebugPrintPower() -     Player EntityID=15 PlayerID={Bob} GameAccountId=[hi=0 lo=0]",
            $"DebugPrintPower() -         tag=CONTROLLER value={Bob}",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=BACON_DUMMY_PLAYER value=1",
            $"DebugPrintGame() - GameType={gameType}",
            $"DebugPrintGame() - PlayerID={Me}, PlayerName=Me#1234",
            $"DebugPrintGame() - PlayerID={Bob}, PlayerName=The Innkeeper",
            .. Entity(20, "TB_BaconShop_HERO_50", Me, "PLAY", "HERO"),
        ];
        Feed(parser, [.. setup, .. bodies]);
        return (parser, tracker);
    }

    private static void Feed(PowerParser parser, params string[] bodies)
    {
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
    }

    private static string[] Entity(int id, string cardId, int controller, string zone, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    /// <summary>A player enchantment as real logs show them: attached to the player entity, in play.</summary>
    private static string[] PlayerEnchant(int id, string cardId, int attack, int health, int attachedTo = 14) =>
        Entity(id, cardId, Me, "PLAY", "ENCHANTMENT", $"tag=ATTACHED value={attachedTo}", $"tag=TAG_SCRIPT_DATA_NUM_1 value={attack}",
            $"tag=TAG_SCRIPT_DATA_NUM_2 value={health}");

    private static string Zone(int id, string cardId, string zone) =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=PLAY zonePos=1 cardId={cardId} player={Me}] tag=ZONE value={zone} ";

    private static string PlayerTag(string tag, int value) => $"DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag={tag} value={value} ";

    private static CounterView Counter((GameState Game, BattlegroundsTracker Tracker) state, string id) =>
        Assert.Single(state.Tracker.BuildView(state.Game).Counters, c => c.Id == id);

    [Fact]
    public void Every_counter_has_a_unique_bg_id_apart_from_the_constructed_ones()
    {
        var ids = BattlegroundsCounters.All.Select(c => c.Id).ToList();

        Assert.Equal(17, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("bg-", id, StringComparison.Ordinal));
        Assert.Empty(ids.Intersect(Counters.All.Select(c => c.Id)));
        var state = Play();
        Assert.Equal(17, state.Tracker.BuildView(state.Game).Counters.Count);
    }

    [Fact]
    public void Unconfirmed_counters_show_a_dash_and_stay_hidden_until_their_data_arrives()
    {
        // A related card in hand does not make an unconfirmed counter relevant: only its tag or enchantment does.
        var state = Play(Entity(30, "BG23_017", Me, "HAND", "MINION"));
        var counters = state.Tracker.BuildView(state.Game).Counters.Where(c => BattlegroundsCounters.Unconfirmed.Contains(c.Id)).ToList();

        Assert.Equal(13, counters.Count);
        Assert.All(counters, c =>
        {
            Assert.Equal(BattlegroundsCounters.NoData, c.Value);
            Assert.False(c.Relevant, c.Id);
        });
    }

    [Fact]
    public void Blood_gem_adds_the_player_bonus_to_the_printed_one()
    {
        var gem = Counter(Play(PlayerTag("BACON_BLOODGEMBUFFATKVALUE", 3), PlayerTag("BACON_BLOODGEMBUFFHEALTHVALUE", 2)), "bg-blood-gem");

        Assert.Equal("+4/+3", gem.Value);
        Assert.True(gem.Relevant);
    }

    [Theory]
    [InlineData("bg-elemental-extra", "BACON_ELEMENTAL_BUFFATKVALUE", "BACON_ELEMENTAL_BUFFHEALTHVALUE", "+2/+1")]
    [InlineData("bg-tavern-spells", "TAVERN_SPELL_ATTACK_INCREASE", "TAVERN_SPELL_HEALTH_INCREASE", "+2/+1")]
    [InlineData("bg-deity", "BACON_OLD_GOD_ATTACK", "BACON_OLD_GOD_HEALTH", "2/1")]
    public void Stat_tags_on_the_player_are_read_by_name(string id, string attackTag, string healthTag, string expected)
    {
        var counter = Counter(Play(PlayerTag(attackTag, 2), PlayerTag(healthTag, 1)), id);

        Assert.Equal(expected, counter.Value);
        Assert.True(counter.Relevant);
    }

    [Fact]
    public void A_tag_the_client_prints_as_a_number_is_not_guessed()
    {
        // Real logs print tags the client has no name for as numbers; 4914/4915 stay unconfirmed.
        var deity = Counter(Play("DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=4914 value=7 ",
            "DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=4915 value=7 "), "bg-deity");

        Assert.Equal(BattlegroundsCounters.NoData, deity.Value);
        Assert.False(deity.Relevant);
    }

    [Fact]
    public void The_deity_chip_is_named_after_the_lobby_deity()
    {
        var deity = Counter(Play("DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=BACON_GLOBAL_OLD_GOD_DBID value=130610 ",
            PlayerTag("BACON_OLD_GOD_ATTACK", 9), PlayerTag("BACON_OLD_GOD_HEALTH", 12)), "bg-deity");

        Assert.Equal("C'Thun", deity.Title);
        Assert.Equal("BGFYM_000", deity.IconCardId);
        Assert.Equal("9/12", deity.Value);
    }

    [Theory]
    [InlineData("bg-undead", "BG25_011pe", 5, 0, "+5")]
    [InlineData("bg-volumizer", "BG34_170pe", 3, 3, "+3/+3")]
    [InlineData("bg-whelp", "BG34_402pe", 6, 6, "+6/+6")]
    [InlineData("bg-beetles", "BG31_808pe", 3, 2, "+3/+2")]
    [InlineData("bg-blood-gem-barrage", "BG34_689e2", 2, 2, "+2/+2")]
    [InlineData("bg-random-tavern", "BG34_854pe", 4, 4, "+4/+4")]
    [InlineData("bg-elemental-tavern", "BG_ShopBuff_Elemental", 1, 1, "+1/+1")]
    public void Player_enchantments_give_their_script_data(string id, string enchantment, int attack, int health, string expected)
    {
        var counter = Counter(Play(PlayerEnchant(40, enchantment, attack, health)), id);

        Assert.Equal(expected, counter.Value);
        Assert.True(counter.Relevant);
    }

    [Fact]
    public void Enchantments_of_several_sources_add_up_and_only_the_players_own_count()
    {
        var state = Play([
            .. PlayerEnchant(40, "BG31_815pe", 1, 1),
            .. PlayerEnchant(41, "BG28_707e", 4, 3),
            // The same enchantment on a minion (entity 50) is not the player's buff.
            .. PlayerEnchant(42, "BG_ShopBuff_Elemental", 9, 9, attachedTo: 50),
        ]);

        Assert.Equal("+5/+4", Counter(state, "bg-elemental-tavern").Value);
    }

    [Fact]
    public void Gold_next_turn_nets_extra_and_overdrawn_gold()
    {
        var gold = Counter(Play(PlayerTag("BACON_PLAYER_EXTRA_GOLD_NEXT_TURN", 3), PlayerTag("BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN", 1)), "bg-gold-next-turn");
        Assert.Equal("+2", gold.Value);
        Assert.True(gold.Relevant);

        // Back to zero after the turn: nothing pending, so Auto hides it again.
        var spent = Counter(Play(PlayerTag("BACON_PLAYER_EXTRA_GOLD_NEXT_TURN", 3), PlayerTag("BACON_PLAYER_EXTRA_GOLD_NEXT_TURN", 0)), "bg-gold-next-turn");
        Assert.Equal("0", spent.Value);
        Assert.False(spent.Relevant);
    }

    [Fact]
    public void Fodder_counts_fodders_in_the_next_refresh()
    {
        var fodder = Counter(Play(PlayerTag("BACON_FODDERS_IN_REFRESH", 2)), "bg-fodder");

        Assert.Equal("2", fodder.Value);
        Assert.True(fodder.Relevant);
    }

    [Fact]
    public void Eternal_knights_count_every_friendly_death_golden_and_combat_copies_included()
    {
        var state = Play([
            .. Entity(30, "BG25_008", Me, "PLAY", "MINION", "tag=COPIED_FROM_ENTITY_ID value=29"),
            .. Entity(31, "BG25_008_G", Me, "PLAY", "MINION"),
            .. Entity(32, "BG25_008", Bob, "PLAY", "MINION"),
            Zone(30, "BG25_008", "GRAVEYARD"),
            Zone(31, "BG25_008_G", "GRAVEYARD"),
            // An enemy Knight dying does not count.
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=32 zone=PLAY zonePos=1 cardId=BG25_008 player=13] tag=ZONE value=GRAVEYARD ",
        ]);

        var knight = Counter(state, "bg-eternal-knight");
        Assert.Equal("+8/+4", knight.Value);
        Assert.True(knight.Relevant);
        Assert.Contains("Golden Eternal Knight: +16/+8.", knight.Details);
    }

    [Fact]
    public void Automatons_count_summons_but_not_combat_copies()
    {
        var state = Play([
            .. Entity(30, "BG_TTN_401", Me, "HAND", "MINION"),
            Zone(30, "BG_TTN_401", "PLAY"),
            // The fight's copy of the same Automaton enters play as a copy.
            .. Entity(31, "BG_TTN_401", Me, "SETASIDE", "MINION", "tag=COPIED_FROM_ENTITY_ID value=30"),
            Zone(31, "BG_TTN_401", "PLAY"),
            // Leaving and re-entering play is still the same summon.
            Zone(30, "BG_TTN_401", "SETASIDE"),
            Zone(30, "BG_TTN_401", "PLAY"),
        ]);

        var automaton = Counter(state, "bg-automaton");
        Assert.Equal("+3/+2", automaton.Value);
        Assert.True(automaton.Relevant);
    }

    [Fact]
    public void Spells_cast_show_when_a_naga_that_cares_is_seen()
    {
        var unseen = Counter(Play(PlayerTag("NUM_SPELLS_PLAYED_THIS_GAME", 4)), "bg-naga-spells");
        Assert.Equal("4", unseen.Value);
        Assert.False(unseen.Relevant);

        var seen = Counter(Play([PlayerTag("NUM_SPELLS_PLAYED_THIS_GAME", 4), .. Entity(30, "BG31_924", Me, "HAND", "MINION")]), "bg-naga-spells");
        Assert.True(seen.Relevant);
        Assert.Equal("BG31_924", seen.IconCardId);
        Assert.Equal(1 / 3.0, seen.Progress!.Value, 3);
    }

    [Fact]
    public void A_related_card_in_the_tavern_keeps_a_confirmed_counter_up_through_combat()
    {
        var (parser, tracker) = Start("GT_BATTLEGROUNDS", Entity(30, "BG25_008", Bob, "PLAY", "MINION"));
        Assert.True(Counter((parser.Game!, tracker), "bg-eternal-knight").Relevant);

        // Combat: the tavern minion leaves and Bob's slot holds the enemy board; what was seen before the fight still counts.
        Feed(parser, [
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=BOARD_VISUAL_STATE value=2 ",
            $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=30 zone=PLAY zonePos=1 cardId=BG25_008 player={Bob}] tag=ZONE value=SETASIDE ",
            .. Entity(31, "BG31_924", Bob, "PLAY", "MINION"),
        ]);
        var view = tracker.BuildView(parser.Game!);

        Assert.True(view.InCombat);
        Assert.True(view.Counters.Single(c => c.Id == "bg-eternal-knight").Relevant);
        Assert.False(view.Counters.Single(c => c.Id == "bg-naga-spells").Relevant);
    }

    [Fact]
    public void Free_refresh_reads_the_reroll_button_cost()
    {
        var free = Counter(Play(Entity(60, "TB_BaconShop_8p_Reroll_Button", Me, "PLAY", "BATTLEGROUND_ACTION", "tag=COST value=0")), "bg-free-refresh");
        Assert.Equal("Free", free.Value);
        Assert.True(free.Relevant);

        var paid = Counter(Play(Entity(60, "TB_BaconShop_8p_Reroll_Button", Me, "PLAY", "BATTLEGROUND_ACTION", "tag=COST value=1")), "bg-free-refresh");
        Assert.Equal("0", paid.Value);
        Assert.False(paid.Relevant);

        // The count tag (enum name, not yet confirmed in a real log) wins when it arrives.
        var counted = Counter(Play([PlayerTag("BACON_FREE_REFRESH_COUNT", 2),
            .. Entity(60, "TB_BaconShop_8p_Reroll_Button", Me, "PLAY", "BATTLEGROUND_ACTION", "tag=COST value=0")]), "bg-free-refresh");
        Assert.Equal("2", counted.Value);
    }

    [Fact]
    public void Constructed_games_have_no_battlegrounds_counters()
    {
        var (parser, tracker) = Start("GT_RANKED", Entity(30, "BG25_008", Me, "HAND", "MINION"));

        Assert.Empty(BattlegroundsCounters.Build(parser.Game!, tracker.Counters, Cards, ["BG25_008"]));
    }
}
