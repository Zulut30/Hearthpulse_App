using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// Sending games to the hearthpulse.net profile without the network: a fake site answers <c>tracker/events/batch</c>,
/// the queue lives in a temp folder, the clock is moved by hand and send passes are run directly.
/// </summary>
public sealed class ProfileUploadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hp-upload-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSite _site = new();
    private readonly HistoryStore _history;
    private DateTime _now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private bool _enabled = true;
    private string? _user = "u-42";
    private string? _token = "acc-1";

    public ProfileUploadTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string QueuePath => Path.Combine(_dir, "profile-upload.json");

    private ProfileUploader Uploader() => new(new AccountClient(new HttpClient(_site), "https://site.test/api/v1/"),
        _ => Task.FromResult(_token), () => _user, () => _enabled, _history, new ProfileUploadQueue(QueuePath))
    {
        UtcNow = () => _now,
    };

    private static int _games;

    private GameRecord Ranked(string result = "Win", DateTime? at = null)
    {
        var start = at ?? new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc).AddMinutes(Interlocked.Increment(ref _games));
        return new GameRecord
        {
            Key = $"{start:yyyyMMddHHmmss}|Secret#1234|GT_RANKED",
            StartedAt = start,
            EndedAt = start.AddMinutes(8),
            Mode = "Ranked",
            GameType = "GT_RANKED",
            Format = "FT_STANDARD",
            PlayerName = "Secret#1234",
            OpponentName = "Rival#5678",
            PlayerClass = "MAGE",
            OpponentClass = "WARRIOR",
            Result = result,
        };
    }

    private GameRecord Saved(GameRecord game)
    {
        Assert.True(_history.Add(game));
        return game;
    }

    private static GameOutcome Outcome(GameRecord game, int? ratingAfter = null) =>
        new(game.Mode, game.Result, game.Placement, game.RatingBefore, ratingAfter, game.HeroCardId) { Key = game.Key };

    [Fact]
    public async Task A_finished_game_goes_to_the_site_once_with_the_account_token()
    {
        var game = Saved(Ranked());
        var uploader = Uploader();

        uploader.OnGameFinished(Outcome(game));
        await uploader.PassAsync(CancellationToken.None);

        var request = Assert.Single(_site.Requests);
        Assert.Equal("Bearer acc-1", request.Authorization);
        var e = Assert.Single(request.Events);
        Assert.Equal(("constructed_match", 2), ((string)e["type"]!, (int)e["schemaVersion"]!));
        Assert.Equal("win", (string)e["payload"]!["result"]!);
        Assert.DoesNotContain("Secret", request.Body, StringComparison.Ordinal);
        Assert.Equal(0, uploader.PendingCount);

        // Raised again (a rating update, a restart replaying the log): settled, not sent again.
        uploader.OnGameFinished(Outcome(game));
        uploader.OnGameFinished(Outcome(game));
        await uploader.PassAsync(CancellationToken.None);
        Assert.Single(_site.Requests);
    }

    [Fact]
    public async Task Nothing_is_queued_or_sent_while_signed_out_or_switched_off()
    {
        var game = Saved(Ranked());
        _user = null;
        var uploader = Uploader();
        uploader.OnGameFinished(Outcome(game));
        await uploader.PassAsync(CancellationToken.None);

        _user = "u-42";
        _enabled = false;
        uploader.OnGameFinished(Outcome(game));
        await uploader.PassAsync(CancellationToken.None);
        Assert.Equal(0, await uploader.BackfillAsync());

        Assert.Empty(_site.Requests);
        Assert.Equal(0, uploader.PendingCount);
    }

    [Fact]
    public async Task Outages_are_retried_with_a_growing_pause_and_the_queue_survives_a_restart()
    {
        var game = Saved(Ranked());
        var uploader = Uploader();
        uploader.OnGameFinished(Outcome(game));
        _site.Offline = true;

        Assert.Equal(TimeSpan.FromSeconds(30), await uploader.PassAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(30), await uploader.PassAsync(CancellationToken.None)); // still waiting, no request
        _now += TimeSpan.FromSeconds(30);
        Assert.Equal(TimeSpan.FromSeconds(60), await uploader.PassAsync(CancellationToken.None));
        _now += TimeSpan.FromSeconds(60);
        _site.Offline = false;
        _site.Status = 503;
        Assert.Equal(TimeSpan.FromSeconds(120), await uploader.PassAsync(CancellationToken.None));
        Assert.Equal(1, uploader.PendingCount);
        Assert.Contains("1 game waiting to be sent.", uploader.Status, StringComparison.Ordinal);

        // The app restarts: the queue is read back from disk and sent.
        _site.Status = 202;
        var restarted = Uploader();
        Assert.Equal(1, restarted.PendingCount);
        await restarted.PassAsync(CancellationToken.None);
        Assert.Equal(0, restarted.PendingCount);
        Assert.Single(_site.Requests, r => r.Status == 202);
        Assert.Equal("All games are on your profile.", restarted.Status);
    }

    [Fact]
    public async Task Rejected_events_are_logged_and_never_retried()
    {
        var good = Saved(Ranked());
        var bad = Saved(Ranked("Loss"));
        var uploader = Uploader();
        uploader.OnGameFinished(Outcome(good));
        uploader.OnGameFinished(Outcome(bad));
        _site.Reject[ProfileEvents.GameEventId(bad)] = ("INVALID_EVENT", "opponentClass");

        await uploader.PassAsync(CancellationToken.None);
        await uploader.PassAsync(CancellationToken.None);
        Assert.Equal(0, await uploader.BackfillAsync());

        Assert.Single(_site.Requests);
        Assert.Equal(0, uploader.PendingCount);
        Assert.Contains($"profile upload: constructed_match {ProfileEvents.GameEventId(bad)} rejected: INVALID_EVENT (opponentClass)", TestDataFolder.ReadLog());
    }

    [Fact]
    public async Task A_refused_batch_is_sent_again_one_event_at_a_time()
    {
        var games = Enumerable.Range(0, 3).Select(_ => Saved(Ranked())).ToList();
        var uploader = Uploader();
        games.ForEach(g => uploader.OnGameFinished(Outcome(g)));
        _site.RefuseBatchesOver = 1;
        _site.RefuseEvent = ProfileEvents.GameEventId(games[1]);

        await uploader.PassAsync(CancellationToken.None);

        Assert.Equal([3, 1, 1, 1], _site.Requests.Select(r => r.Events.Count));
        Assert.Equal([400, 202, 400, 202], _site.Requests.Select(r => r.Status));
        Assert.Equal(0, uploader.PendingCount);
    }

    [Fact]
    public async Task Backfill_sends_past_games_in_batches_of_fifty_and_only_once()
    {
        for (var i = 0; i < 120; i++)
        {
            Saved(Ranked(i % 2 == 0 ? "Win" : "Loss"));
        }

        Saved(Ranked() with { Mode = "Brawl", GameType = "GT_TAVERNBRAWL" });
        var uploader = Uploader();

        Assert.Equal(120, await uploader.BackfillAsync());
        await uploader.PassAsync(CancellationToken.None);

        Assert.Equal([50, 50, 20], _site.Requests.Select(r => r.Events.Count));
        Assert.Equal(120, _site.Requests.SelectMany(r => r.Events).Select(e => (string)e["eventId"]!).Distinct().Count());
        Assert.Equal(0, await uploader.BackfillAsync());
        // A new install of the app (no queue file) backfills the same ids: the site only acknowledges them.
        File.Delete(QueuePath);
        Assert.Equal(120, await Uploader().BackfillAsync());
        Assert.True(_site.Requests.Take(3).SelectMany(r => r.Events).Select(e => (string)e["eventId"]!).ToHashSet()
            .SetEquals(new ProfileUploadQueue(QueuePath).NextBatch(_now, 500, int.MaxValue).Select(e => e.EventId)));
    }

    [Fact]
    public async Task A_battlegrounds_game_waits_for_its_new_rating()
    {
        var start = new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);
        var game = Saved(new GameRecord
        {
            Key = $"{start:yyyyMMddHHmmss}|Secret#1234|GT_BATTLEGROUNDS",
            StartedAt = start,
            EndedAt = start.AddMinutes(20),
            Mode = "Battlegrounds",
            GameType = "GT_BATTLEGROUNDS",
            HeroCardId = "BG20_HERO_242",
            Placement = 3,
            RatingBefore = 6400,
            Result = "Win",
        });
        var uploader = Uploader();

        uploader.OnGameFinished(Outcome(game));
        Assert.Equal(ProfileUploader.RatingWait, await uploader.PassAsync(CancellationToken.None));
        Assert.Empty(_site.Requests);

        _history.UpdateRatingAfter(game.Key, 6431);
        uploader.OnGameFinished(Outcome(game, ratingAfter: 6431));
        await uploader.PassAsync(CancellationToken.None);

        var payload = Assert.Single(Assert.Single(_site.Requests).Events)["payload"]!;
        Assert.Equal((6400, 6431, 3), ((int)payload["ratingBefore"]!, (int)payload["ratingAfter"]!, (int)payload["placement"]!));
    }

    [Fact]
    public async Task A_battlegrounds_game_whose_rating_never_comes_goes_after_the_wait()
    {
        var start = new DateTime(2026, 10, 22, 16, 0, 0, DateTimeKind.Utc);
        var game = Saved(new GameRecord
        {
            Key = $"{start:yyyyMMddHHmmss}|Secret#1234|GT_BATTLEGROUNDS",
            StartedAt = start,
            EndedAt = start.AddMinutes(20),
            Mode = "Battlegrounds",
            GameType = "GT_BATTLEGROUNDS",
            HeroCardId = "BG20_HERO_242",
            Placement = 6,
            RatingBefore = 6400,
            Result = "Loss",
        });
        var uploader = Uploader();
        uploader.OnGameFinished(Outcome(game));

        _now += ProfileUploader.RatingWait;
        await uploader.PassAsync(CancellationToken.None);

        Assert.Null(Assert.Single(Assert.Single(_site.Requests).Events)["payload"]!["ratingAfter"]);
    }

    [Fact]
    public async Task An_arena_game_sends_its_match_and_the_run_score()
    {
        var start = new DateTime(2026, 10, 22, 18, 0, 0, DateTimeKind.Utc);
        GameRecord Arena(int i, string result) => Saved(new GameRecord
        {
            Key = $"{start.AddMinutes(i * 15):yyyyMMddHHmmss}|Secret#1234|GT_ARENA",
            StartedAt = start.AddMinutes(i * 15),
            EndedAt = start.AddMinutes(i * 15 + 10),
            Mode = "Arena",
            GameType = "GT_ARENA",
            PlayerClass = "PALADIN",
            OpponentClass = "ROGUE",
            Result = result,
            DeckId = 9,
        });
        Arena(0, "Win");
        var second = Arena(1, "Loss");
        var uploader = Uploader();

        uploader.OnGameFinished(Outcome(second));
        await uploader.PassAsync(CancellationToken.None);

        var events = Assert.Single(_site.Requests).Events;
        Assert.Equal(["arena_match", "arena_run"], events.Select(e => (string)e["type"]!));
        Assert.Equal((2, 1, 0), ((int)events[0]["payload"]!["gameNumber"]!, (int)events[0]["payload"]!["winsBefore"]!, (int)events[0]["payload"]!["lossesBefore"]!));
        Assert.Equal((1, 1), ((int)events[1]["payload"]!["wins"]!, (int)events[1]["payload"]!["losses"]!));
    }

    [Fact]
    public async Task Another_account_does_not_get_the_first_ones_games()
    {
        var game = Saved(Ranked());
        var uploader = Uploader();
        _site.Offline = true;
        uploader.OnGameFinished(Outcome(game));
        await uploader.PassAsync(CancellationToken.None);
        Assert.Equal(1, uploader.PendingCount);

        _user = "u-77";
        _site.Offline = false;
        _now += TimeSpan.FromHours(1);
        await uploader.PassAsync(CancellationToken.None);

        Assert.Equal(0, uploader.PendingCount);
        Assert.DoesNotContain(_site.Requests, r => r.Status == 202);
        Assert.Equal("u-77", new ProfileUploadQueue(QueuePath).Owner);
    }

    [Fact]
    public void The_queue_replaces_a_waiting_event_and_bounds_its_size()
    {
        var queue = new ProfileUploadQueue(QueuePath);
        queue.Adopt("u-1");
        var e = new ProfileEvent(ProfileEvents.EventId("x"), ProfileEvents.Constructed, "2026-10-22T10:00:00Z", new JsonObject { ["v"] = 1 });
        queue.Enqueue([e], _now.AddMinutes(5));
        queue.Enqueue([e with { Payload = new JsonObject { ["v"] = 2 } }], _now);

        var batch = Assert.Single(new ProfileUploadQueue(QueuePath).NextBatch(_now, 50, int.MaxValue));
        Assert.Equal(2, (int)batch.Payload["v"]!);

        queue.Enqueue(Enumerable.Range(0, ProfileUploadQueue.MaxPending + 10)
            .Select(i => new ProfileEvent(ProfileEvents.EventId("n" + i), ProfileEvents.Constructed, "2026-10-22T10:00:00Z", new JsonObject())), _now);
        Assert.Equal(ProfileUploadQueue.MaxPending, queue.PendingCount);
        // Byte limit: a batch stops before it would grow past it, but always carries one event.
        Assert.Single(queue.NextBatch(_now, 50, 10));
    }

    /// <summary>A fake <c>tracker/events/batch</c>: accepts, rejects or refuses events and records every request.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        public List<(string Authorization, string Body, List<JsonObject> Events, int Status)> Requests { get; } = [];

        public Dictionary<string, (string Code, string Field)> Reject { get; } = [];

        public bool Offline { get; set; }

        public int Status { get; set; } = 202;

        /// <summary>Batches larger than this are refused with 400 INVALID_BATCH.</summary>
        public int RefuseBatchesOver { get; set; } = 50;

        /// <summary>An event refused with 400 even alone.</summary>
        public string? RefuseEvent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            Assert.Equal("https://site.test/api/v1/tracker/events/batch", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var events = JsonNode.Parse(body)!["events"]!.AsArray();
            var ids = events.Select(e => (string)e!["eventId"]!).ToList();
            var status = Status != 202 ? Status
                : events.Count > RefuseBatchesOver || (RefuseEvent is not null && ids.Contains(RefuseEvent)) ? 400
                : 202;
            Requests.Add((request.Headers.Authorization?.ToString() ?? string.Empty, body, [.. events.Select(e => e!.AsObject())], status));

            var answer = status switch
            {
                202 => new JsonObject
                {
                    ["accepted"] = new JsonArray([.. ids.Where(id => !Reject.ContainsKey(id)).Select(id => (JsonNode)id)]),
                    ["rejected"] = new JsonArray([.. ids.Where(Reject.ContainsKey).Select(id => (JsonNode)new JsonObject
                    {
                        ["eventId"] = id, ["code"] = Reject[id].Code, ["field"] = Reject[id].Field,
                    })]),
                },
                400 => new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_BATCH", ["message"] = "bad" } },
                _ => new JsonObject { ["error"] = new JsonObject { ["code"] = "SERVER_ERROR", ["message"] = "down" } },
            };
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }
}
