using System.Globalization;
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
/// asked by name (L-M2), a rating only for one row spelled exactly like the name, "?" for anything less certain and
/// "&lt;Floor" for no row (L-m1, L-m5), nothing from a partial table (L-m6), off by default, and the names kept out of
/// the history and the log.
/// </summary>
public sealed class BgOpponentMmrTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 23, 0, 10, 0, DateTimeKind.Utc);

    /// <summary>A Europe game account: kind 2, region 2, program "WTCG".</summary>
    private const ulong EuAccountHi = 0x0200000257544347;

    /// <summary>The fixture's local player (PlayerID=2, "Player#1000").</summary>
    private const int LocalId = 2;

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
    public void A_name_spelled_exactly_gives_its_rating_and_in_another_case_a_question_mark()
    {
        var board = new BgLeaderboard([("Alice", 9012), ("Борис", 8500)]);

        Is(BgMmrStatus.Found, 9012, board.Match("Alice"));
        Is(BgMmrStatus.Found, 8500, board.Match("Борис"));
        // Names are not unique: one that differs only in case may be another player (L-m5).
        Is(BgMmrStatus.Ambiguous, null, board.Match("aLiCe"));
        Is(BgMmrStatus.Ambiguous, null, board.Match("БОРИС"));
        Is(BgMmrStatus.Ambiguous, null, board.Match("борис"));
    }

    [Fact]
    public void A_name_several_rows_share_gets_no_rating()
    {
        // Not the better one, as Firestone does: it could be the other player's rating (L-m1).
        var board = new BgLeaderboard([("Bob", 12000), ("bob", 8100), ("Яна", 9000), ("Яна", 8800)]);

        Is(BgMmrStatus.Ambiguous, null, board.Match("Bob"));
        Is(BgMmrStatus.Ambiguous, null, board.Match("bob"));
        Is(BgMmrStatus.Ambiguous, null, board.Match("Яна"));
    }

    [Fact]
    public void A_name_not_on_the_table_is_below_its_floor_and_no_name_is_unknown()
    {
        var board = new BgLeaderboard([("Alice", 9012), ("Bob", 8003)]);

        Assert.Equal(8003, board.Floor);
        var below = board.Match("Carol");
        Is(BgMmrStatus.BelowThreshold, null, below);
        Assert.Equal(8003, below.Floor);
        Assert.Equal(8003, board.Match("Alice").Floor);
        Assert.Equal(BgMmrStatus.Unknown, board.Match(null).Status);
        Assert.Equal(BgMmrStatus.Unknown, board.Match("  ").Status);
    }

    [Fact]
    public void An_empty_or_partial_table_matches_nobody()
    {
        // A missing name says nothing about a player when the table is empty (a new season) or cut short (L-m6).
        var empty = new BgLeaderboard([]);
        Assert.False(empty.Complete);
        Assert.Null(empty.Floor);
        Is(BgMmrStatus.Unknown, null, empty.Match("Alice"));

        var partial = new BgLeaderboard([("Alice", 9012)], complete: false);
        Is(BgMmrStatus.Unknown, null, partial.Match("Alice"));
        Is(BgMmrStatus.Unknown, null, partial.Match("Carol"));
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
        Assert.True(board.Complete);
        Assert.Equal(250, board.Count);
        Assert.Equal(19751, board.Floor);
        Assert.Equal("2026-10-29", board.Day);
        Is(BgMmrStatus.Found, 19995, board.Match("Player5"));
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
    public async Task A_cached_table_is_used_until_its_snapshot_is_a_day_old()
    {
        var server = new Server(total: 120);
        var source = Source(server);
        await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(2, server.Requests.Count);

        Assert.Equal(120, (await source.LoadAsync("battlegrounds", "EU"))!.Count);
        Assert.Equal(2, server.Requests.Count);

        source.Clock = () => DateTime.UtcNow.AddDays(1).AddHours(1);
        server.Day = "2026-10-30";
        server.CapturedAt = DateTime.UtcNow.AddDays(1);
        var renewed = await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal("2026-10-30", renewed!.Day);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task The_age_counts_from_the_snapshot_and_an_unchanged_snapshot_costs_one_request()
    {
        // The server took this snapshot 23 hours ago: downloaded now, it is stale in an hour, not in a day.
        var server = new Server(total: 120) { CapturedAt = DateTime.UtcNow.AddHours(-23) };
        var source = Source(server);
        await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(2, server.Requests.Count);

        source.Clock = () => DateTime.UtcNow.AddHours(2);
        var again = await source.LoadAsync("battlegrounds", "EU");
        Assert.Equal(120, again!.Count);
        // The server has no newer snapshot yet: its first page says so, the rest is not downloaded again.
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task Offline_a_cached_table_up_to_a_week_old_stands_in()
    {
        var server = new Server(total: 30);
        var source = Source(server);
        await source.LoadAsync("battlegroundsduo", "US");
        server.Offline = true;

        source.Clock = () => DateTime.UtcNow.AddDays(3);
        Assert.Equal(30, (await source.LoadAsync("battlegroundsduo", "US"))!.Count);

        source.Clock = () => DateTime.UtcNow.AddDays(8);
        Assert.Null(await source.LoadAsync("battlegroundsduo", "US"));
    }

    [Fact]
    public async Task An_offline_copy_is_asked_for_again_every_half_hour_until_the_server_answers()
    {
        // A table three days old on disk, then the server down.
        await Source(new Server(total: 30) { CapturedAt = DateTime.UtcNow.AddDays(-3) }).LoadAsync("battlegrounds", "EU");
        var server = new Server(total: 40) { Offline = true };
        var source = Source(server);
        var now = DateTime.UtcNow;
        source.Clock = () => now;
        var changed = new SemaphoreSlim(0);
        source.Changed += () => changed.Release();

        Assert.Null(source.TryGet("battlegrounds", "EU", out _));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(30, source.TryGet("battlegrounds", "EU", out var loading)!.Count);
        Assert.False(loading);

        server.Offline = false;
        now += TimeSpan.FromMinutes(5);
        Assert.Equal(30, source.TryGet("battlegrounds", "EU", out loading)!.Count);
        Assert.False(loading);

        now += TimeSpan.FromMinutes(26);
        source.TryGet("battlegrounds", "EU", out loading);
        Assert.True(loading);
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(40, source.TryGet("battlegrounds", "EU", out _)!.Count);
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
    public async Task An_empty_table_matches_nobody_and_is_not_cached()
    {
        var source = Source(new Server(total: 0));

        var board = await source.LoadAsync("battlegrounds", "EU");

        Assert.False(board!.Complete);
        Is(BgMmrStatus.Unknown, null, board.Match("Player1"));
        Assert.False(File.Exists(CacheFile("battlegrounds", "eu")));
    }

    [Fact]
    public async Task A_table_cut_short_matches_nobody_and_is_not_cached()
    {
        // The second page is short while the first said 250 rows.
        var source = Source(new Server(total: 250) { ShortPageAt = 100 });

        var board = await source.LoadAsync("battlegrounds", "EU");

        Assert.False(board!.Complete);
        Is(BgMmrStatus.Unknown, null, board.Match("Player5"));
        Is(BgMmrStatus.Unknown, null, board.Match("Nobody"));
        Assert.False(File.Exists(CacheFile("battlegrounds", "eu")));
    }

    [Fact]
    public async Task A_table_past_the_page_bound_is_not_whole()
    {
        var server = new Server(total: BgLeaderboardSource.MaxPages * BgLeaderboardSource.PageSize + 50);
        var source = Source(server);

        var board = await source.LoadAsync("battlegrounds", "EU");

        Assert.Equal(BgLeaderboardSource.MaxPages, server.Requests.Count);
        Assert.False(board!.Complete);
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
    public void Off_by_default_no_names_are_read_no_ratings_shown_and_nothing_is_downloaded()
    {
        Assert.False(new Hearthpulse.Core.AppSettings().ShowBgOpponentMmr);
        var server = new Server(total: 30);
        _session.SetBgLeaderboards(Source(server));
        StartGame();
        Tick();
        Tick();

        Assert.False(_session.WantLobbyNames);
        var opponents = Opponents();
        Assert.NotEmpty(opponents);
        Assert.All(opponents, o =>
        {
            Assert.Null(o.PlayerName);
            Assert.Null(o.Mmr);
        });
        Assert.Empty(server.Requests);
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
        names[ids[0]] = "Alpha";
        names[ids[1]] = "Beta";
        // ids[2] has no name in memory yet.
        _session.SetLobbyNames(names);
        Tick();

        var view = Opponents().ToDictionary(o => o.PlayerId);
        Assert.Equal("Alpha", view[ids[0]].PlayerName);
        Is(BgMmrStatus.Found, 9000, view[ids[0]].Mmr);
        Is(BgMmrStatus.Ambiguous, null, view[ids[1]].Mmr);
        Assert.Null(view[ids[2]].PlayerName);
        Assert.Equal(BgMmrStatus.Unknown, view[ids[2]].Mmr!.Status);
        Assert.All(ids.Skip(3), id =>
        {
            Is(BgMmrStatus.BelowThreshold, null, view[id].Mmr);
            Assert.Equal(8200, view[id].Mmr!.Floor);
        });
        // A name still missing keeps the memory read going; with every name it stops.
        Assert.True(_session.WantLobbyNames);

        // Each memory scan gives a new map.
        _session.SetLobbyNames(new Dictionary<int, string>(names) { [ids[2]] = "Delta" });
        Tick();
        Assert.False(_session.WantLobbyNames);
    }

    [Fact]
    public void Two_players_of_the_lobby_with_one_name_both_get_a_question_mark()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        var ids = Opponents().Select(o => o.PlayerId).ToList();
        _session.SetLobbyNames(new Dictionary<int, string> { [ids[0]] = "Alpha", [ids[1]] = "ALPHA", [ids[2]] = "Gamma", [ids[3]] = "Delta", [ids[4]] = "delta" });
        Tick();

        var view = Opponents().ToDictionary(o => o.PlayerId);
        // The one "Alpha" row could be either one's (L-m1); a shared name gets "?" also when no row has it.
        Is(BgMmrStatus.Ambiguous, null, view[ids[0]].Mmr);
        Is(BgMmrStatus.Ambiguous, null, view[ids[1]].Mmr);
        Is(BgMmrStatus.BelowThreshold, null, view[ids[2]].Mmr);
        Is(BgMmrStatus.Ambiguous, null, view[ids[3]].Mmr);
        Is(BgMmrStatus.Ambiguous, null, view[ids[4]].Mmr);
    }

    [Fact]
    public void An_opponent_with_the_players_own_name_gets_a_question_mark()
    {
        _session.SetBgLeaderboards(SourceWith(new BgLeaderboard([("Alpha", 9000), ("Player", 9500), ("Omega", 8800)])));
        _session.SetBgOpponentMmrEnabled(true);
        StartGame();
        var ids = Opponents().Select(o => o.PlayerId).ToList();
        // The player is "Alpha" in memory and "Player#1000" in Power.log.
        _session.SetLobbyNames(new Dictionary<int, string> { [LocalId] = "Alpha", [ids[0]] = "Alpha", [ids[1]] = "Player", [ids[2]] = "Omega" });
        Tick();

        var view = Opponents().ToDictionary(o => o.PlayerId);
        Is(BgMmrStatus.Ambiguous, null, view[ids[0]].Mmr);
        Is(BgMmrStatus.Ambiguous, null, view[ids[1]].Mmr);
        Is(BgMmrStatus.Found, 8800, view[ids[2]].Mmr);
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
        Is(BgMmrStatus.Found, 9000, view[ids[0]].Mmr);
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
        _session.SetLobbyNames(new Dictionary<int, string> { [id] = "Player3" });
        Tick();
        Assert.Equal(BgMmrStatus.Loading, Opponents().Single(o => o.PlayerId == id).Mmr!.Status);

        server.Hold.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Opponents().Single(o => o.PlayerId == id).Mmr!.Status == BgMmrStatus.Loading && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Tick();
        }

        Is(BgMmrStatus.Found, 19997, Opponents().Single(o => o.PlayerId == id).Mmr);
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

    private static void Is(BgMmrStatus status, int? rating, BgOpponentMmr? mmr)
    {
        Assert.NotNull(mmr);
        Assert.Equal((status, rating), (mmr.Status, mmr.Rating));
    }

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
    /// The leaderboard endpoint over <c>total</c> rows named "Player{n}" rated 20000 - n, one snapshot; records every
    /// request, before anything else (an offline server too). <see cref="Hold"/> keeps answers back until it is completed.
    /// </summary>
    private sealed class Server(int total) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool Offline { get; set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public int FailOffset { get; set; } = -1;

        /// <summary>The page at this offset holds half a page although <c>total</c> says more.</summary>
        public int ShortPageAt { get; set; } = -1;

        public string Day { get; set; } = "2026-10-29";

        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

        public TaskCompletionSource? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.RequestUri!.PathAndQuery);
            }

            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var offset = int.Parse(query["offset"]!);
            if (Status != HttpStatusCode.OK || offset == FailOffset)
            {
                var status = Status != HttpStatusCode.OK ? Status : HttpStatusCode.InternalServerError;
                return new HttpResponseMessage(status) { Content = new StringContent("{\"detail\":\"no\"}") };
            }

            var size = Math.Max(0, Math.Min(offset == ShortPageAt ? 50 : 100, total - offset));
            var rows = Enumerable.Range(offset, size)
                .Select(n => new { rank = n + 1, name = "Player" + n, rating = 20000.0 - n, previous_rank = (int?)null });
            var body = JsonSerializer.Serialize(new
            {
                data = new
                {
                    board = new { region = query["region"], mode = query["mode"], season_id = 19, rated = true },
                    snapshot = new { day = Day, captured_at = CapturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture), total_size = total },
                    total,
                    rows,
                },
                meta = new { count = total },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
