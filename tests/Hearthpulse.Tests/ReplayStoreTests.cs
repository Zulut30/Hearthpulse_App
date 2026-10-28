using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

public sealed class ReplayStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-replays-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void File_name_comes_from_the_game_key()
    {
        var name = ReplayStore.FileNameFor("20261023163353|velox#21276|GT_RANKED");
        Assert.StartsWith("20261023163353-", name);
        Assert.EndsWith(".hpr", name);
        Assert.Equal(name, ReplayStore.FileNameFor("20261023163353|velox#21276|GT_RANKED"));
        Assert.NotEqual(name, ReplayStore.FileNameFor("20261023163353|other#1|GT_RANKED"));
        Assert.StartsWith("game-", ReplayStore.FileNameFor("../../evil|x"));
    }

    [Fact]
    public void Saves_compressed_and_reads_back()
    {
        var store = new ReplayStore(_dir);
        var text = ReplayFile.Write(new ReplayHeader { LocalRank = "Gold 4", OpponentRank = "Gold 2" }, Enumerable.Range(0, 2000).Select(i => $"E {i} L DECK 0 - -"));

        var name = store.Save("20261023120000|a|GT_RANKED", text);

        Assert.NotNull(name);
        Assert.True(new FileInfo(Path.Combine(_dir, name!)).Length < text.Length / 4);
        Assert.Equal(text, store.Load(name));
        var (header, body) = ReplayFile.Read(store.Load(name)!)!.Value;
        Assert.Equal("Gold 2", header.OpponentRank);
        Assert.Equal(2000, body.Count);
    }

    [Fact]
    public void A_saved_replay_is_kept_unless_overwritten()
    {
        var store = new ReplayStore(_dir);
        var name = store.Save("20261023120000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["T 1 L 0"]));
        store.Save("20261023120000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["T 2 L 0"]));
        Assert.Contains("T 1 L 0", store.Load(name));

        store.Save("20261023120000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["T 3 L 0"]), overwrite: true);
        Assert.Contains("T 3 L 0", store.Load(name));
    }

    [Fact]
    public void Old_replays_go_first_when_the_folder_is_full()
    {
        var store = new ReplayStore(_dir) { MaxTotalFiles = 3 };
        var names = Enumerable.Range(1, 5)
            .Select(i => store.Save($"2026102312000{i}|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), [$"T {i} L 0"]))!)
            .ToList();

        Assert.Null(store.Load(names[0]));
        Assert.Null(store.Load(names[1]));
        Assert.All(names.Skip(2), n => Assert.NotNull(store.Load(n)));
    }

    [Fact]
    public void Load_refuses_paths_outside_the_folder_and_missing_files()
    {
        var store = new ReplayStore(_dir);
        Assert.Null(store.Load(null));
        Assert.Null(store.Load("..\\history.db"));
        Assert.Null(store.Load("../x.hpr"));
        Assert.Null(store.Load("missing.hpr"));
        Assert.Null(ReplayFile.Read("not a replay"));
    }

    [Fact]
    public void Battlegrounds_data_round_trips()
    {
        var data = new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 3, Tier = 2, Health = 30, Board = [new ReplayMinion("BG_X", 2, 3, true)], Shop = ["BG_A", "BG_B"], Result = "Win" }],
            Lobby = [new BattlegroundsLobbyHero("TB_BaconShop_HERO_08", 1, true)],
        };
        var (header, body) = ReplayFile.Read(ReplayFile.WriteBattlegrounds(new ReplayHeader(), data))!.Value;
        Assert.Equal(ReplayFile.Battlegrounds, header.Kind);
        var back = ReplayFile.ReadBattlegrounds(body)!;
        Assert.Equal("BG_X", back.Rounds[0].Board[0].CardId);
        Assert.True(back.Rounds[0].Board[0].Golden);
        Assert.Equal(["BG_A", "BG_B"], back.Rounds[0].Shop);
        Assert.True(back.Lobby[0].Local);
    }
}
