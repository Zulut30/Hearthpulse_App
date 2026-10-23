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
