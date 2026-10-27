using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Xunit;
using static Hearthpulse.Tests.ReplayRecorderTests;

namespace Hearthpulse.Tests;

/// <summary>The match page model both apps bind to: header, steps from either side, decks, Battlegrounds rounds.</summary>
public sealed class MatchPageTests : IDisposable
{
    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-match-page-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    private static GameRecord Game(string? replay, string mode = "Ranked") => new()
    {
        Key = "20261023123000|velox#21276|GT_RANKED",
        StartedAt = new DateTime(2026, 10, 23, 12, 30, 0),
        EndedAt = new DateTime(2026, 10, 23, 12, 41, 5),
        Mode = mode,
        PlayerName = "velox#21276",
        OpponentName = "Rival#1234",
        PlayerClass = "PRIEST",
        OpponentClass = "WARRIOR",
        Result = "Win",
        Turns = 9,
        WentFirst = false,
        Replay = replay,
    };

    [Fact]
    public void A_game_without_a_replay_says_why()
    {
        var page = MatchPage.Load(Game(null), _store, CardDb.Empty, null);
        Assert.Contains("before Hearthpulse recorded replays", page.Missing);
        Assert.Null(page.Step(0, ReplayPlayer.Local));
        Assert.Equal("Priest vs Warrior", page.Header.Title);
        Assert.Equal("Rival", page.Header.Opponent.Name);
        Assert.Contains(page.Header.Facts, f => f.Value == "You had the Coin");

        var gone = MatchPage.Load(Game("missing.hpr"), _store, CardDb.Empty, null);
        Assert.Contains("gone", gone.Missing);
    }

    [Fact]
    public void The_table_turns_around_and_their_hand_stays_card_backs()
    {
        Record(out var recorder,
        [
            .. Card(62, "", "DECK", 2, ""),
            Zone(62, "HAND"),
            .. Card(66, "BE_012", "PLAY", 1, "MINION", "tag=ATK value=3", "tag=HEALTH value=2"),
        ]);
        var name = _store.Save(Game(null).Key, recorder.Text(new ReplayHeader { OpponentRank = "Gold 2" }));
        var page = MatchPage.Load(Game(name), _store, CardDb.Empty, null);

        Assert.Null(page.Missing);
        Assert.Equal("Gold 2", page.Header.Opponent.Rank);
        var last = page.FrameCount - 1;
        var mine = page.Step(last, ReplayPlayer.Local)!;
        Assert.True(mine.Bottom.IsLocal);
        Assert.Equal("BE_012", Assert.Single(mine.Bottom.Board).CardId);
        var back = Assert.Single(mine.Top.Hand);
        Assert.True(back.CardBack);
        Assert.Null(back.CardId);
        Assert.Equal(string.Empty, back.Name);

        var theirs = page.Step(last, ReplayPlayer.Opponent)!;
        Assert.False(theirs.Bottom.IsLocal);
        Assert.True(theirs.Top.IsLocal);
        Assert.Contains(page.Actions(last), a => a.Current);
    }

    [Fact]
    public void Analysis_reads_health_board_mana_key_cards_and_the_mulligan()
    {
        string[] events =
        [
            "T 1 L 0",
            "E 1 L PLAY 0 HERO_09 HERO HEALTH=30",
            "E 2 O PLAY 0 HERO_08 HERO HEALTH=30",
            "E 3 L HAND 1 CS2_029 SPELL COST=4",
            "E 4 L HAND 2 BE_012 MINION ATK=3 COST=2 HEALTH=2",
            "S BEGIN_MULLIGAN",
            "V L MULLIGAN_STATE INPUT",
            "C MULLIGAN - CS2_029,BE_012",
            "c BE_012",
            "V L MULLIGAN_STATE DEALING",
            "X 3",
            "E 5 L HAND 1 CS2_182 MINION ATK=4 COST=4 HEALTH=5",
            "V L MULLIGAN_STATE DONE",
            "S MAIN_READY",
            "V L RESOURCES 2",
            "B",
            "E 4 L PLAY 1 BE_012 MINION ATK=3 COST=2 HEALTH=2",
            "P 4 -",
            "V L RESOURCES_USED 2",
            "A 4 2",
            "V 2 DAMAGE 3",
            "T 2 O 30",
            "V O RESOURCES 2",
            "B",
            "E 6 O PLAY 1 CS2_120 MINION ATK=2 COST=2 HEALTH=3",
            "P 6 -",
            "V O RESOURCES_USED 1",
            "A 6 4",
            "V 4 DAMAGE 2",
            "V 6 DAMAGE 3",
            "E 4 L GRAVEYARD 0 BE_012 MINION ATK=3 COST=2 HEALTH=2",
            "E 6 O GRAVEYARD 0 CS2_120 MINION ATK=2 COST=2 HEALTH=3",
            "T 3 L 60",
            "V L RESOURCES 3",
            "V L RESOURCES_USED 0",
            "R WON",
        ];
        var replay = MatchReplay.Parse(ReplayFile.Write(new ReplayHeader(), events))!;
        var analysis = MatchAnalysis.Build(Game(null), replay, CardDb.Empty, null);

        Assert.Equal("3", analysis.Tiles[0].Value);
        Assert.Equal([30.0, 30.0], analysis.LocalHealth.Take(2).Select(h => h!.Value));
        Assert.Equal([27.0, 27.0], analysis.OpponentHealth.Take(2).Select(h => h!.Value));
        Assert.Equal([1.0], analysis.LocalMana);
        Assert.Equal([0.5], analysis.OpponentMana);
        Assert.Equal(("2/2", "1/2"), (analysis.TurnRows[0].Mana, analysis.TurnRows[1].Mana));
        Assert.Equal("1 · You", analysis.TurnRows[0].Label);
        var key = Assert.Single(analysis.KeyCards, k => k.Local);
        Assert.Equal("BE_012", key.CardId);
        Assert.Equal("Turn 1 · +5 board", key.Reason);
        Assert.Equal("BE_012", replay.Frames[key.Frame].Action.CardId);
        Assert.Equal(["Replaced", "Kept"], analysis.Mulligan.Select(m => m.Choice));
        Assert.Equal(["CS2_182"], analysis.MulliganReceived);
        Assert.Contains("community meta", analysis.MulliganNote);
        Assert.Empty(analysis.Meta);

        // The match page stars that one action in the turn's list, with the reason as its tooltip; the header says won.
        var page = MatchPage.Load(Game(_store.Save(Game(null).Key, ReplayFile.Write(new ReplayHeader(), events))), _store, CardDb.Empty, null);
        Assert.True(page.Header.Won);
        Assert.False(page.Header.Lost);
        var marked = Assert.Single(page.Actions(key.Frame), a => a.Key);
        Assert.Equal((key.Frame, "BE_012", true, "Turn 1 · +5 board"), (marked.Frame, marked.CardId, marked.Local, marked.KeyReason));
        Assert.All(page.Actions(key.Frame).Where(a => !a.Key), a => Assert.Null(a.KeyReason));
    }

    [Fact]
    public void Battlegrounds_rounds_and_lobby_are_shown()
    {
        var data = new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 4, Tier = 3, Health = 28, Board = [new ReplayMinion("BG_X", 4, 5, false)], Result = "Loss", DamageTaken = 6 }],
            Lobby = [new BattlegroundsLobbyHero("TB_BaconShop_HERO_08", 3, true), new BattlegroundsLobbyHero("TB_BaconShop_HERO_01", 1, false)],
        };
        var game = Game(null, "Battlegrounds") with { Placement = 3, HeroCardId = "TB_BaconShop_HERO_08" };
        var name = _store.Save(game.Key, ReplayFile.WriteBattlegrounds(new ReplayHeader(), data));
        var page = MatchPage.Load(game with { Replay = name }, _store, CardDb.Empty, null);

        Assert.True(page.IsBattlegrounds);
        Assert.Equal("3rd place", page.Header.Result);
        var round = Assert.Single(page.Rounds);
        Assert.Equal(("Lost · 6 damage", "Loss"), (round.Result, round.ResultKey));
        Assert.Equal(4, Assert.Single(round.Board).Attack);
        Assert.Equal(["3rd", "1st"], page.Lobby.Select(l => l.Place));
    }
}
