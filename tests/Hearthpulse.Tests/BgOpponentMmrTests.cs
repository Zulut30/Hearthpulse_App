using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// Battlegrounds opponents' MMR (docs/bg-opponent-mmr.md): the region's public leaderboard downloaded whole and never
/// asked by name (L-M2), names matched locally with "?" for a shared name and "&lt;8000" for none (L-m1), off by
/// default, and the names kept out of the history and the log.
/// </summary>
public sealed class BgOpponentMmrTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 23, 0, 10, 0, DateTimeKind.Utc);

    /// <summary>A Europe game account: kind 2, region 2, program "WTCG".</summary>
    private const ulong EuAccountHi = 0x0200000257544347;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hp-bgmmr-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    private readonly HearthstoneMemory _memory = new();
    private readonly HistoryStore _history;
    private readonly GameSession _session;
    private DateTime _now = T0;
    private bool _running = true;

    public BgOpponentMmrTests()
    {
        Directory.CreateDirectory(_folder);
        _db = Path.Combine(_folder, "history.db");
        _history = new HistoryStore(_db);
        _session = new GameSession(_history, _memory)
        {
            Clock = () => _now,
            IsHearthstoneRunning = () => _running,
            ReadsMemory = false,
        };
        _session.OnSessionStarted("Hearthstone_2026_10_23_02_01_43", new DateTime(2026, 10, 23, 2, 1, 43), attachedMidSession: false);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- matching -------------------------------------------------------------------------------

    [Fact]
    public void One_row_gives_its_rating_whatever_the_case()
    {
        var board = new BgLeaderboard([("Alice", 9012), ("Борис", 8500)]);

        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 9012), board.Match("aLiCe"));
        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 8500), board.Match("БОРИС"));
    }

    [Fact]
    public void A_name_two_rows_share_gets_no_rating()
    {
        // Not the better one, as Firestone does: it could be the other player's rating (L-m1).
        var board = new BgLeaderboard([("Bob", 12000), ("bob", 8100)]);

        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Ambiguous, null), board.Match("Bob"));
    }

    [Fact]
    public void A_name_not_on_the_table_is_below_its_floor_and_no_name_is_unknown()
    {
        var board = new BgLeaderboard([("Alice", 9012)]);

        Assert.Equal(new BgOpponentMmr(BgMmrStatus.BelowThreshold, null), board.Match("Carol"));
        Assert.Equal(BgMmrStatus.Unknown, board.Match(null).Status);
        Assert.Equal(BgMmrStatus.Unknown, board.Match("  ").Status);
    }

    [Theory]
    [InlineData("Alice#1234", "Alice")]
    [InlineData(" Alice ", "Alice")]
    [InlineData("Al#ice", "Al#ice")]
    [InlineData("Alice#", "Alice#")]
    [InlineData("UNKNOWN HUMAN PLAYER", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Lobby_names_are_spelled_as_the_leaderboard_spells_them(string? raw, string? expected) =>
        Assert.Equal(expected, HearthstoneMemory.LobbyName(raw));

    [Theory]
    [InlineData(0x0200000157544347UL, "US")]
    [InlineData(0x0200000257544347UL, "EU")]
    [InlineData(0x0200000357544347UL, "AP")]
    [InlineData(0x0200000457544347UL, "AP")]
    [InlineData(0x0200000557544347UL, null)]
    [InlineData(1UL, null)]
    [InlineData(0UL, null)]
    public void The_region_comes_from_the_game_account(ulong hi, string? region) =>
        Assert.Equal(region, BgLeaderboardSource.RegionOf(hi));

    // ---- loading the table ----------------------------------------------------------------------

    [Fact]
    public async Task The_whole_table_is_read_page_by_page_and_never_by_name()
    {
        var server = new Server(total: 250);
        var source = Source(server);

        var board = await source.LoadAsync("battlegrounds", "EU");

        Assert.NotNull(board);
        Assert.Equal(250, board.Count);
        Assert.Equal("2026-10-29", board.Day);
        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 19995), board.Match("player5"));
        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, r =>
        {
            Assert.StartsWith("/v1/leaderboards?mode=battlegrounds&region=EU&limit=100&offset=", r);
            Assert.DoesNotContain("name=", r);
            Assert.DoesNotContain("q=", r);
            Assert.DoesNotContain("/player", r);
        });
        Assert.DoesNotContain("day=", server.Requests[0]);
        Assert.EndsWith("offset=100&day=2026-10-29", server.Requests[1]);
        Assert.EndsWith("offset=200&day=2026-10-29", server.Requests[2]);
    }

    [Fact]
    public async Task A_cached_table_is_used_for_a_day()
    {
        var server = new Server(total: 120);
        var source = Source(server);
        await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(2, server.Requests.Count);

        var again = await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(120, again!.Count);
        Assert.Equal(2, server.Requests.Count);

        File.SetLastWriteTimeUtc(CacheFile("battlegrounds", "eu"), DateTime.UtcNow.AddDays(-2));
        await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task Offline_a_cached_table_up_to_a_week_old_stands_in()
    {
        var server = new Server(total: 30);
        var source = Source(server);
        await source.LoadAsync("battlegroundsduo", "US");
        server.Offline = true;

        File.SetLastWriteTimeUtc(CacheFile("battlegroundsduo", "us"), DateTime.UtcNow.AddDays(-3));
        Assert.Equal(30, (await source.LoadAsync("battlegroundsduo", "US"))!.Count);

        File.SetLastWriteTimeUtc(CacheFile("battlegroundsduo", "us"), DateTime.UtcNow.AddDays(-8));
        Assert.Null(await source.LoadAsync("battlegroundsduo", "US"));
    }

    [Fact]
    public async Task A_table_the_server_does_not_have_is_remembered_for_a_day()
    {
        var server = new Server(total: 30) { Status = HttpStatusCode.NotFound };
        var source = Source(server);

        Assert.Null(await source.LoadAsync("battlegrounds", "AP"));
        Assert.Null(await source.LoadAsync("battlegrounds", "AP"));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_failed_page_fails_the_whole_load_and_leaves_no_cache()
    {
        var server = new Server(total: 250) { FailOffset = 100 };
        var source = Source(server);

        Assert.Null(await source.LoadAsync("battlegrounds", "EU"));
        Assert.False(File.Exists(CacheFile("battlegrounds", "eu")));
    }

    [Fact]
    public async Task Unknown_modes_and_regions_are_not_asked_for()
    {
        var server = new Server(total: 30);
        var source = Source(server);

        Assert.Null(await source.LoadAsync("standard", "EU"));
        Assert.Null(await source.LoadAsync("battlegrounds", "CN"));
        Assert.Null(await source.LoadAsync("battlegrounds", "EU&name=x"));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TryGet_loads_once_in_the_background_and_says_when_it_is_done()
    {
        var server = new Server(total: 30) { Hold = new TaskCompletionSource() };
        var source = Source(server);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += () => changed.TrySetResult();

        Assert.Null(source.TryGet("battlegrounds", "EU", out var loading));
        Assert.True(loading);
        Assert.Null(source.TryGet("battlegrounds", "EU", out loading));
        Assert.True(loading);

        server.Hold.SetResult();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(30, source.TryGet("battlegrounds", "EU", out loading)!.Count);
        Assert.False(loading);
        Assert.Single(server.Requests);
    }

    // ---- the live view --------------------------------------------------------------------------

    [Fact]
    public void Off_by_default_no_names_are_read_and_no_ratings_shown()
    {
        Assert.False(new Hearthpulse.Core.AppSettings().ShowBgOpponentMmr);
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000)])));
        StartGame();

        Assert.False(_session.WantLobbyNames);
        var opponents = Opponents();
        Assert.NotEmpty(opponents);
        Assert.All(opponents, o =>
        {
            Assert.Null(o.PlayerName);
            Assert.Null(o.Mmr);
        });
    }

    [Fact]
    public void On_each_opponent_gets_a_rating_a_question_mark_or_below_the_floor()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000), ("Beta", 8200), ("beta", 12000)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        Assert.True(_session.WantLobbyNames);

        var ids = Opponents().Select(o => o.PlayerId).ToList();
        var names = ids.Skip(3).ToDictionary(id => id, id => "Gamma" + id);
        names[ids[0]] = "alpha";
        names[ids[1]] = "Beta";
        // ids[2] has no name in memory yet.
        _session.SetLobbyNames(names);
        Tick();

        var view = Opponents().ToDictionary(o => o.PlayerId);
        Assert.Equal("alpha", view[ids[0]].PlayerName);
        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 9000), view[ids[0]].Mmr);
        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Ambiguous, null), view[ids[1]].Mmr);
        Assert.Null(view[ids[2]].PlayerName);
        Assert.Equal(BgMmrStatus.Unknown, view[ids[2]].Mmr!.Status);
        Assert.All(ids.Skip(3), id => Assert.Equal(new BgOpponentMmr(BgMmrStatus.BelowThreshold, null), view[id].Mmr));
        // A name still missing keeps the memory read going; with every name it stops.
        Assert.True(_session.WantLobbyNames);

        // Each memory scan gives a new map.
        _session.SetLobbyNames(new Dictionary<int, string>(names) { [ids[2]] = "Delta" });
        Tick();
        Assert.False(_session.WantLobbyNames);
    }

    [Fact]
    public void A_scan_with_fewer_names_does_not_take_back_a_name()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        var ids = Opponents().Select(o => o.PlayerId).ToList();
        _session.SetLobbyNames(new Dictionary<int, string> { [ids[0]] = "Alpha" });
        Tick();

        // A scan cut short, then one that found the next name.
        _session.SetLobbyNames(new Dictionary<int, string>());
        _session.SetLobbyNames(new Dictionary<int, string> { [ids[1]] = "Omega" });
        Tick();

        var view = Opponents().ToDictionary(o => o.PlayerId);
        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 9000), view[ids[0]].Mmr);
        Assert.Equal("Omega", view[ids[1]].PlayerName);
        Assert.Equal(BgMmrStatus.BelowThreshold, view[ids[1]].Mmr!.Status);
    }

    [Fact]
    public void While_the_table_downloads_the_opponents_show_loading()
    {
        var server = new Server(total: 30) { Hold = new TaskCompletionSource() };
        _session.SetBgLeaderboards(Source(server));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        var id = Opponents()[0].PlayerId;
        _session.SetLobbyNames(new Dictionary<int, string> { [id] = "player3" });
        Tick();
        Assert.Equal(BgMmrStatus.Loading, Opponents().Single(o => o.PlayerId == id).Mmr!.Status);

        server.Hold.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Opponents().Single(o => o.PlayerId == id).Mmr!.Status == BgMmrStatus.Loading && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Tick();
        }

        Assert.Equal(new BgOpponentMmr(BgMmrStatus.Found, 19997), Opponents().Single(o => o.PlayerId == id).Mmr);
    }

    [Fact]
    public void Without_a_region_or_without_memory_the_rating_is_unknown()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000)])));
        _session.SetBgOpponentMmrEnabled(true);

        // The fixture's account id is a placeholder: no region, so no table to match.
        StartGame(accountHi: 1);
        var id = Opponents()[0].PlayerId;
        _session.SetLobbyNames(new Dictionary<int, string> { [id] = "Alpha" });
        Tick();
        Assert.Equal(BgMmrStatus.Unknown, Opponents()[0].Mmr!.Status);
    }

    [Fact]
    public void Names_not_read_yet_without_memory_reads_are_unknown_and_turning_off_forgets_them()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        Assert.All(Opponents(), o => Assert.Equal(BgMmrStatus.Unknown, o.Mmr!.Status));

        var id = Opponents()[0].PlayerId;
        _session.SetLobbyNames(new Dictionary<int, string> { [id] = "Alpha" });
        Tick();
        Assert.Equal(BgMmrStatus.Found, Opponents().Single(o => o.PlayerId == id).Mmr!.Status);

        _session.SetBgOpponentMmrEnabled(false);
        Tick();
        Assert.All(Opponents(), o => Assert.Null(o.PlayerName));

        _session.SetBgOpponentMmrEnabled(true);
        Tick();
        Assert.All(Opponents(), o => Assert.Null(o.PlayerName));
    }

    [Fact]
    public void Names_and_ratings_never_reach_the_history_or_the_log()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Zqmmrsecretalpha", 9876)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        var ids = Opponents().Select(o => o.PlayerId).ToList();
        _session.SetLobbyNames(ids.ToDictionary(id => id, id => id == ids[0] ? "Zqmmrsecretalpha" : "Zqmmrsecret" + id));
        Tick();
        Assert.Equal(9876, Opponents().Single(o => o.PlayerId == ids[0]).Mmr!.Rating);

        // The fixture ends with the client closed: the game is saved as abandoned after the grace time.
        _running = false;
        Tick(TimeSpan.FromSeconds(3));
        Tick(GameSession.AbandonAfter + TimeSpan.FromSeconds(10));
        var game = Assert.Single(_history.Recent());
        var row = JsonSerializer.Serialize(game);
        Assert.DoesNotContain("Zqmmrsecret", row, StringComparison.OrdinalIgnoreCase);

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_folder, "history.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.DoesNotContain("Zqmmrsecret", reader.ReadToEnd(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Zqmmrsecret", TestDataFolder.ReadLog(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private BgLeaderboardSource Source(Server server) =>
        new(new HearthpulseApi(new HttpClient(server), "https://api.test/"), _folder);

    private BgLeaderboardSource SourceWith(BgLeaderboard board)
    {
        var source = Source(new Server(total: 0) { Offline = true });
        source.Set("battlegrounds", "EU", board);
        return source;
    }

    private string CacheFile(string mode, string region) => Path.Combine(_folder, $"bg-leaderboard-{mode}-{region}.json");

    /// <summary>The real Battlegrounds fixture up to its third round, with the local player's account in <paramref name="accountHi"/>'s region.</summary>
    private void StartGame(ulong accountHi = EuAccountHi)
    {
        var lines = BattlegroundsFixtureTests.Lines()
            .Select(l => l.Replace("PlayerID=2 GameAccountId=[hi=1 lo=1]", $"PlayerID=2 GameAccountId=[hi={accountHi} lo=1]", StringComparison.Ordinal))
            .ToList();
        _session.OnLines("Power.log", lines, catchingUp: false);
        Tick();
    }

    private void Tick(TimeSpan? step = null)
    {
        _now += step ?? TimeSpan.FromSeconds(1);
        _session.OnTick();
    }

    private IReadOnlyList<BgOpponent> Opponents() => _session.Current.Battlegrounds?.Opponents ?? [];

    /// <summary>
    /// The leaderboard endpoint over <c>total</c> rows named "Player{n}" rated 20000 - n, one snapshot day; records every
    /// request. <see cref="Hold"/> keeps answers back until it is completed.
    /// </summary>
    private sealed class Server(int total) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool Offline { get; set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public int FailOffset { get; set; } = -1;

        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.PathAndQuery;
            lock (Requests)
            {
                Requests.Add(path);
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var offset = int.Parse(query["offset"]!);
            if (Status != HttpStatusCode.OK || offset == FailOffset)
            {
                var status = Status != HttpStatusCode.OK ? Status : HttpStatusCode.InternalServerError;
                return new HttpResponseMessage(status) { Content = new StringContent("{\"detail\":\"no\"}") };
            }

            var rows = Enumerable.Range(offset, Math.Max(0, Math.Min(100, total - offset)))
                .Select(n => new { rank = n + 1, name = "Player" + n, rating = 20000.0 - n, previous_rank = (int?)null });
            var body = JsonSerializer.Serialize(new
            {
                data = new
                {
                    board = new { region = query["region"], mode = query["mode"], season_id = 19, rated = true },
                    snapshot = new { day = "2026-10-29", total_size = total },
                    total,
                    rows,
                },
                meta = new { count = total },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
