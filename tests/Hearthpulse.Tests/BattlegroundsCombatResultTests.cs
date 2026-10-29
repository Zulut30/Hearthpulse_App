using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// The result and damage of each Battlegrounds combat (history, the overlay's last combats, the match page's rounds):
/// decided by the fight itself - the hero attack that closes it, else the boards - not by how the lobby's health moved
/// afterwards. Synthetic lines follow the shapes of the 29.10 log, where the opponent's golden Wrath Weaver hurt its own
/// hero in the batch that follows our tie on turn 2 and the tracker called that tie a win.
/// </summary>
public sealed class BattlegroundsCombatResultTests
{
    private const int Opponent = 4;

    [Fact]
    public void A_tie_stays_a_tie_when_the_opponent_hurts_itself_after_the_fight()
    {
        var (_, _, tracker) = Play([
            .. Setup,
            .. CombatStart(),
            // Both minions trade: nobody is left standing, no hero attack.
            Start("ATTACK", 101), Tag(111, "DAMAGE", 4), Tag(101, "DAMAGE", 6), End,
            Start("DEATHS", 1), Tag(111, "ZONE", "GRAVEYARD"), Tag(101, "ZONE", "GRAVEYARD"), End,
            Game("BOARD_VISUAL_STATE", 1),
            // The lobby catches up after the fight: the opponent's own Wrath Weaver cost it 2.
            Tag(20, "DAMAGE", 2),
        ]);

        var combat = Assert.Single(tracker.Combats);
        Assert.Equal("Tie", combat.Result);
        Assert.Equal(0, combat.DamageDealt);
        Assert.Equal(0, combat.DamageTaken);
    }

    [Fact]
    public void A_win_counts_only_the_hero_attack_and_our_own_damage_next_turn_does_not_turn_it_into_a_loss()
    {
        var (_, _, tracker) = Play([
            .. Setup,
            .. CombatStart(),
            Start("ATTACK", 101), Tag(111, "DAMAGE", 2), Tag(101, "DAMAGE", 1), End,
            Start("DEATHS", 1), Tag(111, "ZONE", "GRAVEYARD"), End,
            // The hero attack after the fight sits in the game's own block in real logs.
            Start("TRIGGER", 46), Start("ATTACK", 10), Game("PROPOSED_DEFENDER", 110), Tag(110, "DAMAGE", 5), End, End,
            Game("BOARD_VISUAL_STATE", 1),
            // Our 5 and 2 of its own reach the opponent's lobby hero together.
            Tag(20, "DAMAGE", 7),
            Game("TURN", 3),
            // Next tavern: our own Wrath Weaver costs us 3 before the next combat.
            Tag(10, "DAMAGE", 3),
        ]);

        var combat = Assert.Single(tracker.Combats);
        Assert.Equal("Win", combat.Result);
        Assert.Equal(5, combat.DamageDealt);
        Assert.Equal(0, combat.DamageTaken);
    }

    [Fact]
    public void A_loss_takes_the_damage_of_the_hero_attack()
    {
        var (_, _, tracker) = Play([
            .. Setup,
            .. CombatStart(),
            Start("ATTACK", 111), Tag(101, "DAMAGE", 6), Tag(111, "DAMAGE", 4), End,
            Start("DEATHS", 1), Tag(101, "ZONE", "GRAVEYARD"), End,
            Start("TRIGGER", 46), Start("ATTACK", 110), Game("PROPOSED_DEFENDER", 10), Tag(10, "DAMAGE", 4), End, End,
            Game("BOARD_VISUAL_STATE", 1),
            Tag(20, "DAMAGE", 1),
        ]);

        var combat = Assert.Single(tracker.Combats);
        Assert.Equal("Loss", combat.Result);
        Assert.Equal(4, combat.DamageTaken);
        Assert.Equal(0, combat.DamageDealt);
    }

    /// <summary>Turn 1 ends, the combat starts against lobby player 4: our 4/6 against their hero and a 1/2.</summary>
    private static string[] CombatStart() =>
    [
        .. Entity(101, "BG26_801", Me, "PLAY", "MINION", "ATK=4", "HEALTH=6", "ZONE_POSITION=1"),
        Game("TURN", 2),
        Game("BOARD_VISUAL_STATE", 2),
        Tag(3, "BACON_CURRENT_COMBAT_PLAYER_ID", Opponent),
        Tag(50, "ZONE", "REMOVEDFROMGAME"), Tag(51, "ZONE", "REMOVEDFROMGAME"), Tag(52, "ZONE", "REMOVEDFROMGAME"),
        Game("STEP", "MAIN_READY"),
        Start("TRIGGER", 46),
        Tag(30, "ZONE", "SETASIDE"),
        .. Entity(110, "BG20_HERO_102", Bob, "PLAY", "HERO", "HEALTH=30", $"PLAYER_ID={Opponent}"),
        .. Entity(111, "BGS_119", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=2", "ZONE_POSITION=1"),
        End,
        Game("STEP", "MAIN_START"),
    ];
}
