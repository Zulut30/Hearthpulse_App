using Hearthpulse.Core.Replay;
using Xunit;
using static Hearthpulse.Tests.ReplayRecorderTests;

namespace Hearthpulse.Tests;

/// <summary>The replay model: frames, turns and actions folded from recorded events.</summary>
public sealed class MatchReplayTests
{
    private static MatchReplay Fold(params string[] bodies)
    {
        Record(out var recorder, bodies);
        return MatchReplay.Parse(recorder.Text(new ReplayHeader { LocalRank = "Gold 4" }))!;
    }

    private const string PlayBlock =
        "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={0} zone=HAND zonePos=1 cardId= player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

    [Fact]
    public void Their_play_is_a_step_with_the_table_after_it()
    {
        var replay = Fold(
        [
            .. Card(62, "", "DECK", 2, ""),
            Zone(62, "HAND"),
            string.Format(PlayBlock, 62),
            .. Show(62, "CS2_182", "tag=CARDTYPE value=MINION", "tag=ATK value=4", "tag=HEALTH value=5"),
            Zone(62, "PLAY", "CS2_182"),
            "DebugPrintPower() - BLOCK_END",
        ]);

        Assert.Equal("Gold 4", replay.Header.LocalRank);
        var play = Assert.Single(replay.Frames, f => f.Action.Kind == ReplayActionKind.Play);
        Assert.Equal(ReplayPlayer.Opponent, play.Action.Player);
        Assert.Equal("CS2_182", play.Action.CardId);
        var minion = Assert.Single(play.Opponent.Minions);
        Assert.Equal((4, 5), (minion.Attack, minion.Health));
        Assert.Empty(play.Opponent.Hand);
        Assert.Equal(1, play.Opponent.DeckCount);

        // The step before shows the card back in their hand, without a name.
        var before = replay.Frames[play.Index - 1];
        var back = Assert.Single(before.Opponent.Hand);
        Assert.True(back.IsCardBack);
        Assert.Equal("drawn", back.Origin);
        Assert.Equal(new ShownCard("CS2_182", 3, 1), Assert.Single(replay.OpponentPlayed));
    }

    [Fact]
    public void A_location_used_from_the_table_is_not_a_card_played()
    {
        var replay = Fold(
        [
            .. Card(68, "TLC_100", "PLAY", 2, "LOCATION", "tag=HEALTH value=3"),
            "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=L id=68 zone=PLAY zonePos=1 cardId=TLC_100 player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            Tag(68, "DAMAGE", "1"),
            "DebugPrintPower() - BLOCK_END",
        ]);

        var use = Assert.Single(replay.Frames, f => f.Action.Kind == ReplayActionKind.Use);
        Assert.Equal("TLC_100", use.Action.CardId);
        Assert.Empty(replay.OpponentPlayed);
        Assert.Equal(2, Assert.Single(use.Opponent.Minions).Health);
    }

    [Fact]
    public void An_attack_records_damage_and_the_death_it_caused()
    {
        var replay = Fold(
        [
            .. Card(66, "BE_012", "PLAY", 1, "MINION", "tag=ATK value=3", "tag=HEALTH value=2"),
            .. Card(67, "CS2_120", "PLAY", 2, "MINION", "tag=ATK value=2", "tag=HEALTH value=3"),
            "DebugPrintPower() - BLOCK_START BlockType=ATTACK Entity=[entityName=X id=66 zone=PLAY zonePos=1 cardId=BE_012 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            "DebugPrintPower() -     TAG_CHANGE Entity=GameEntity tag=PROPOSED_DEFENDER value=67 ",
            Tag(67, "DAMAGE", "3"),
            Tag(66, "DAMAGE", "2", player: 1),
            "DebugPrintPower() - BLOCK_END",
            Zone(67, "GRAVEYARD", "CS2_120"),
            Zone(66, "GRAVEYARD", "BE_012", player: 1),
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=6 ",
        ]);

        var attack = Assert.Single(replay.Frames, f => f.Action.Kind == ReplayActionKind.Attack);
        Assert.Equal(("BE_012", "CS2_120"), (attack.Action.CardId, attack.Action.TargetId));
        Assert.Equal(2, attack.Action.Deaths.Count);
        Assert.Contains(new ReplayDeath(ReplayPlayer.Opponent, "CS2_120", "MINION"), attack.Action.Deaths);
        Assert.Empty(attack.Local.Minions);
        Assert.Empty(attack.Opponent.Minions);
        Assert.Contains(replay.Frames, f => f.Action.Kind == ReplayActionKind.TurnStart && f.Turn == 6);
    }

    [Fact]
    public void Discover_choices_keep_the_options_and_the_pick()
    {
        var replay = Fold(
        [
            .. Card(70, "TLC_515", "SETASIDE", 1, "SPELL"),
            .. Card(71, "CS2_029", "SETASIDE", 1, "SPELL"),
            "DebugPrintEntityChoices() - id=3 Player=velox#21276 TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1",
            "DebugPrintEntityChoices() -   Source=[entityName=X id=99 zone=HAND zonePos=1 cardId=CORE_CS2_004 player=1]",
            "DebugPrintEntityChoices() -   Entities[0]=[entityName=A id=70 zone=SETASIDE zonePos=0 cardId=TLC_515 player=1]",
            "DebugPrintEntityChoices() -   Entities[1]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=1]",
            "DebugPrintEntitiesChosen() - id=3 Player=velox#21276 EntitiesCount=1",
            "DebugPrintEntitiesChosen() -   Entities[0]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=1]",
            Zone(71, "HAND", "CS2_029", player: 1),
        ]);

        var choice = Assert.Single(replay.Choices);
        Assert.Equal("CORE_CS2_004", choice.CardId);
        Assert.Equal(["TLC_515", "CS2_029"], choice.Options);
        Assert.Equal(["CS2_029"], choice.Picked);
        var frame = replay.Frames.Single(f => f.Action.Kind == ReplayActionKind.Choice);
        Assert.Contains(frame.Local.Hand, c => c.CardId == "CS2_029");
    }

    [Fact]
    public void Not_a_constructed_replay_is_not_parsed()
    {
        Assert.Null(MatchReplay.Parse(ReplayFile.WriteBattlegrounds(new ReplayHeader(), new BattlegroundsReplayData())));
        Assert.Null(MatchReplay.Parse("garbage"));
    }

    [Fact]
    public void A_damaged_line_is_skipped()
    {
        var replay = MatchReplay.Parse(ReplayFile.Write(new ReplayHeader(), ["T 3 L 10", "E x L PLAY", "E 1 L PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "R WON"]))!;
        Assert.Equal("WON", replay.Result);
        Assert.Single(replay.Frames[^1].Local.Minions);
    }
}
