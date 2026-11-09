using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.SourceCapture.Processing;

namespace Hearthpulse.Tests;

/// <summary>Source wire v2 and result v2 (contracts C1/C2, ADR 0013). Synthetic sources and the checked-in BG fixture only.</summary>
public sealed class SourceCaptureV2Tests : IDisposable
{
    private readonly string _folder = TestDataFolder.Scratch("hp-source-v2");
    public SourceCaptureV2Tests() => Directory.CreateDirectory(_folder);
    public void Dispose() => TestDataFolder.TryDelete(_folder);

    // AC2: real BG hero pick -> v2 source -> bg_hero offer fact and place 3, still not complete for aggregation.
    [Fact]
    public async Task Checked_in_bg_game_with_synthetic_end_yields_hero_offer_and_placement_facts()
    {
        var (frames, game) = RecordFixture(end: true);
        var capture = frames.GroupBy(f => f.CaptureId).Single(g => g.Any(f => f.Record.Kind == "end" && Data(f).GetProperty("reason").GetString() == "normal")).ToArray();
        Assert.All(capture, f => Assert.Equal(2, f.Record.V));
        var start = Data(capture[0]);
        Assert.Equal("bg", start.GetProperty("mode").GetString());
        Assert.True(game.BuildNumber > 0);
        Assert.Equal(game.BuildNumber, start.GetProperty("build").GetInt32());
        Assert.Equal(JsonValueKind.Null, start.GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Null, start.GetProperty("arena").ValueKind);
        var gaps = capture.Where(f => f.Record.Kind == "gap").Select(f => Data(f).GetProperty("reason").GetString()).ToArray();
        Assert.Single(gaps, r => r == "combat_view");
        // The header (records before the first observation) carries no unconditional source_missing any more.
        Assert.DoesNotContain("source_missing", capture.Skip(1).TakeWhile(f => f.Record.Kind == "gap").Select(f => Data(f).GetProperty("reason").GetString()));
        var firstChoice = capture.First(f => f.Record.Kind == "choice");
        Assert.Equal("mulligan", Data(firstChoice).GetProperty("choiceType").GetString());

        var result = await Decode(capture);
        Assert.Equal(2, result.V);
        Assert.Equal(2, result.SourceVersion);
        Assert.Equal("source-v2.0", result.ParserVersion);
        Assert.Equal(new SourceCaptureContext("bg", start.GetProperty("day").GetInt32(), game.BuildNumber, null, null), result.Context);
        Assert.True(result.Eligibility.Outcome);
        Assert.True(result.Eligibility.Offers);
        Assert.Empty(result.Eligibility.Reasons);
        Assert.Equal(3, result.Facts.Outcome!.Placement);
        Assert.Equal("L", result.Facts.Outcome.Result);
        var offer = Assert.Single(result.Facts.Offers);
        Assert.Equal("bg_hero", offer.Kind);
        var resolved = capture.Last(f => f.Record.Kind == "choice" && Data(f).GetProperty("choiceId").GetInt32() == Data(firstChoice).GetProperty("choiceId").GetInt32());
        Assert.True(Data(resolved).GetProperty("resolved").GetBoolean());
        Assert.Equal(resolved.Record.Seq, offer.Seq);
        Assert.InRange(offer.Options.Count, 2, 4);
        Assert.Equal(Data(resolved).GetProperty("options").EnumerateArray().Select(x => x.GetString()), offer.Options);
        Assert.Single(offer.Picked);
        Assert.Equal(Data(resolved).GetProperty("picked").EnumerateArray().Select(x => x.GetString()), offer.Picked);
        Assert.Equal("partial", result.Status);
        Assert.False(result.Summary.CompleteForAggregation);
    }

    // AC3 through the live recorder on the real BG game: switched on mid-game, or closed by shutdown.
    [Theory]
    [InlineData(true, true, "lifecycle_gap")]
    [InlineData(false, false, "end_reason")]
    public async Task Live_recorder_late_attach_or_shutdown_gives_no_outcome_and_no_offers(bool late, bool end, string reason)
    {
        var (frames, _) = RecordFixture(end, late);
        Assert.NotEmpty(frames);
        var capture = frames.GroupBy(f => f.CaptureId).Last().ToArray();
        Assert.Equal("start", capture[0].Record.Kind);
        if (late) Assert.Contains(capture, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "late_attach");
        else Assert.Equal("shutdown", Data(capture[^1]).GetProperty("reason").GetString());
        var result = await Decode(capture);
        Assert.False(result.Eligibility.Outcome);
        Assert.False(result.Eligibility.Offers);
        Assert.Contains(reason, result.Eligibility.Reasons);
        Assert.Contains("lifecycle_gap", result.Eligibility.Reasons);
        Assert.Null(result.Facts.Outcome);
        Assert.Empty(result.Facts.Offers);
    }

    // AC3: a late attach or a shutdown makes neither fact eligible.
    [Theory]
    [InlineData("late_attach", "normal", "lifecycle_gap")]
    [InlineData("shutdown", "shutdown", "end_reason")]
    [InlineData("rotation", "rotation", "lifecycle_gap")]
    public async Task Lifecycle_gaps_and_abnormal_end_make_outcome_and_offers_ineligible(string gap, string reason, string expected)
    {
        var result = await Parse(Source(Start("bg"), Gap(1, gap), Gap(2, "combat_view"), Hero(3, resolved: false), Hero(4, resolved: true),
            End(5, result: reason == "normal" ? "L" : null, placement: reason == "normal" ? 3 : null, reason: reason)));
        Assert.False(result.Eligibility.Outcome);
        Assert.False(result.Eligibility.Offers);
        Assert.Contains(expected, result.Eligibility.Reasons);
        Assert.Null(result.Facts.Outcome);
        Assert.Empty(result.Facts.Offers);
    }

    [Fact]
    public async Task A_clean_v2_bg_source_is_eligible_but_other_gaps_still_keep_it_partial()
    {
        var result = await Parse(Source(Start("bg"), Gap(1, "combat_view"), Hero(2, resolved: false), Hero(3, resolved: true),
            Gap(4, "unsupported"), Gap(5, "source_missing"), End(6, placement: 3)));
        Assert.True(result.Eligibility.Outcome);
        Assert.True(result.Eligibility.Offers);
        Assert.Equal(3, result.Facts.Offers[0].Seq);
        Assert.Equal(new[] { "HERO_A", "HERO_B", "HERO_C", "HERO_D" }, result.Facts.Offers[0].Options);
        Assert.Equal(new[] { "HERO_B" }, result.Facts.Offers[0].Picked);
        Assert.Equal("partial", result.Status);
        Assert.False(result.Summary.CompleteForAggregation);
    }

    [Theory]
    [InlineData("general", true, 4, 1, "no_offer")]
    [InlineData("mulligan", false, 4, 0, "offer_unresolved")]
    [InlineData("mulligan", true, 1, 1, "offer_shape")]
    [InlineData("mulligan", true, 5, 1, "offer_shape")]
    public async Task Offer_eligibility_requires_a_resolved_first_mulligan_with_two_to_four_options_and_one_pick(
        string type, bool resolved, int options, int picks, string reason)
    {
        var heroes = Enumerable.Range(0, options).Select(i => "HERO_" + (char)('A' + i)).ToArray();
        var data = JsonSerializer.Serialize(new { choiceId = 1, sourceCard = (string?)null, options = heroes, picked = heroes.Take(picks).ToArray(), resolved, choiceType = type });
        var result = await Parse(Source(Start("bg"), Gap(1, "combat_view"), Line(2, 2, "choice", data, side: "local"), End(3, placement: 2)));
        Assert.True(result.Eligibility.Outcome);
        Assert.False(result.Eligibility.Offers);
        Assert.Equal(new[] { reason }, result.Eligibility.Reasons);
        Assert.Empty(result.Facts.Offers);
        Assert.Equal(2, result.Facts.Outcome!.Placement);
    }

    [Fact]
    public async Task Constructed_v2_has_an_outcome_but_no_offers_and_a_draft_has_neither()
    {
        var ranked = await Parse(Source(Start("ranked", format: "wild"), End(1, result: "T")));
        Assert.Equal(new SourceCaptureContext("ranked", 20766, 123, "wild", null), ranked.Context);
        Assert.True(ranked.Eligibility.Outcome);
        Assert.False(ranked.Eligibility.Offers);
        Assert.Equal(new[] { "no_offer" }, ranked.Eligibility.Reasons);
        Assert.Equal(new SourceCaptureOutcome("T", null), ranked.Facts.Outcome);
        var draft = await Parse(Source(Start("arena", kind: "draft", arena: "underground"),
            Line(2, 1, "draft_choice", """{"pick":1,"options":[{"card":"A","package":[]}],"picked":0,"underground":true}""", side: "local"), End(2, result: null)));
        Assert.Equal("underground", draft.Context.Arena);
        Assert.False(draft.Eligibility.Outcome);
        Assert.False(draft.Eligibility.Offers);
        Assert.Equal(new[] { "draft" }, draft.Eligibility.Reasons);
    }

    // AC4: v1 stays readable; v1 and v2 never mix; v2-only fields are rejected in v1.
    [Fact]
    public async Task V1_sources_decode_with_v1_eligibility_rules()
    {
        var ranked = await Parse(Source(Start("ranked", v: 1), End(1, result: "W", v: 1)));
        Assert.Equal(2, ranked.V);
        Assert.Equal(1, ranked.SourceVersion);
        Assert.Equal(new SourceCaptureContext("ranked", 20766, 123, null, null), ranked.Context);
        Assert.True(ranked.Summary.CompleteForAggregation);
        Assert.True(ranked.Eligibility.Outcome);
        Assert.False(ranked.Eligibility.Offers);
        Assert.Equal(new[] { "source_v1" }, ranked.Eligibility.Reasons);
        Assert.Equal(new SourceCaptureOutcome("W", null), ranked.Facts.Outcome);
        var partial = await Parse(Source(Start("ranked", v: 1), Gap(1, "unsupported", v: 1), End(2, result: "W", v: 1)));
        Assert.False(partial.Summary.CompleteForAggregation);
        Assert.False(partial.Eligibility.Outcome);
        var bg = await Parse(Source(Start("bg", v: 1), End(1, placement: 1, v: 1)));
        Assert.True(bg.Summary.CompleteForAggregation);
        Assert.False(bg.Eligibility.Outcome);
        Assert.Equal(new[] { "source_v1" }, bg.Eligibility.Reasons);
        Assert.Null(bg.Facts.Outcome);
        Assert.Empty(bg.Facts.Offers);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task Mixed_versions_v2_fields_in_v1_and_misplaced_v2_context_fail_closed(int scenario)
    {
        string source = scenario switch
        {
            0 => Source(Start("ranked", v: 1), End(1, v: 2)),
            1 => Source(Start("ranked"), End(1, v: 1)),
            2 => Source(Start("bg", v: 1), Line(1, 1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A","B"],"picked":[],"resolved":false,"choiceType":"mulligan"}""", side: "local"), End(2, placement: 1, v: 1)),
            3 => Source(Start("bg", v: 1), Gap(1, "combat_view", v: 1), End(2, placement: 1, v: 1)),
            4 => Source(Start("bg"), End(1, placement: 1)), // a v2 BG game always names combat_view
            5 => Source(Start("bg"), Gap(1, "combat_view"), Gap(2, "combat_view"), End(3, placement: 1)),
            6 => Source(Start("bg"), Hero(1, resolved: false), Gap(2, "combat_view"), End(3, placement: 1)),
            7 => Source(Start("bg", format: "standard"), Gap(1, "combat_view"), End(2, placement: 1)),
            _ => Source(Start("ranked", arena: "normal"), End(1)),
        };
        var error = await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(source));
        Assert.Equal("invalid_source", error.Code);
    }

    [Fact]
    public async Task A_choice_type_cannot_change_within_one_choice()
    {
        var other = Line(2, 3, "choice", """{"choiceId":1,"sourceCard":null,"options":["HERO_A","HERO_B","HERO_C","HERO_D"],"picked":["HERO_B"],"resolved":true,"choiceType":"general"}""", side: "local");
        var error = await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(Source(Start("bg"), Gap(1, "combat_view"), Hero(2, true), other, End(4, placement: 1))));
        Assert.Equal("invalid_source", error.Code);
    }

    // AC6: the decoder and the worker CLI are byte-stable on a v2 source.
    [Fact]
    public async Task V2_result_and_cli_output_are_byte_identical_across_runs()
    {
        string source = Source(Start("bg"), Gap(1, "combat_view"), Hero(2, false), Hero(3, true), End(4, placement: 5));
        Assert.Equal(SourceWorkerCommand.Serialize(await Parse(source)), SourceWorkerCommand.Serialize(await Parse(source)));
        string input = Path.Combine(_folder, "source.jsonl"), first = Path.Combine(_folder, "a.json"), second = Path.Combine(_folder, "b.json");
        await File.WriteAllTextAsync(input, source, new UTF8Encoding(false));
        using var status = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--input", input, "--output", first], status));
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--input", input, "--output", second], status));
        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(first));
        var root = json.RootElement;
        Assert.Equal(new[] { "v", "parserVersion", "sourceVersion", "sourceSha256", "status", "records", "unsupported", "gaps", "events", "summary", "context", "eligibility", "facts" },
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("source-v2.0", root.GetProperty("parserVersion").GetString());
        Assert.Equal("""{"mode":"bg","day":20766,"build":123,"format":null,"arena":null}""", root.GetProperty("context").GetRawText());
        Assert.Equal("""{"outcome":true,"offers":true,"reasons":[]}""", root.GetProperty("eligibility").GetRawText());
        Assert.Equal("""{"outcome":{"result":null,"placement":5},"offers":[{"seq":3,"kind":"bg_hero","options":["HERO_A","HERO_B","HERO_C","HERO_D"],"picked":["HERO_B"]}]}""",
            root.GetProperty("facts").GetRawText());
    }

    // AC5: the start record carries the public format and the Arena kind, nothing else.
    [Theory]
    [InlineData("GT_RANKED", "FT_STANDARD", "ranked", "standard", null)]
    [InlineData("GT_CASUAL", "FT_WILD", "casual", "wild", null)]
    [InlineData("GT_RANKED", "FT_FUTURE", "ranked", null, null)]
    [InlineData("GT_UNDERGROUND_ARENA", "FT_WILD", "arena", null, "underground")]
    [InlineData("GT_ARENA", "FT_STANDARD", "arena", null, "normal")]
    [InlineData("GT_TAVERNBRAWL", "FT_WILD", "brawl", null, null)]
    public void Game_start_carries_format_and_arena_kind(string gameType, string formatType, string mode, string? format, string? arena)
    {
        var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add;
        var game = SourceCaptureRecorderTests.Game(gameType); game.FormatType = formatType;
        recorder.SetEnabled(true); recorder.OnGameStarted(game);
        recorder.OnEntityCreated(game, SourceCaptureRecorderTests.Entity(game, 49, "LOCAL_SEED", "HAND", 1, "MINION"));
        var start = Data(frames[0]);
        Assert.Equal("start", frames[0].Record.Kind);
        Assert.Equal(2, frames[0].Record.V);
        Assert.Equal(mode, start.GetProperty("mode").GetString());
        Assert.Equal(format, start.GetProperty("format").GetString());
        Assert.Equal(arena, start.GetProperty("arena").GetString());
        Assert.DoesNotContain(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "combat_view");
    }

    [Theory]
    [InlineData(true, "underground")]
    [InlineData(false, "normal")]
    [InlineData(null, null)]
    public async Task Draft_capture_start_names_the_arena_kind_from_the_offer(bool? underground, string? arena)
    {
        var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add;
        recorder.SetEnabled(true);
        recorder.CaptureDraftChoice(new ArenaDraftOffer(["DRAFT_A", "DRAFT_B", "DRAFT_C"], underground), 2, 0, new(2026, 11, 9));
        Assert.Equal(arena, Data(frames[0]).GetProperty("arena").GetString());
        var result = await Decode(frames);
        Assert.Equal(arena, result.Context.Arena);
        Assert.Equal(new[] { "draft" }, result.Eligibility.Reasons);
    }

    // AC8 (queue side): a v1 capture spooled before the update is sealed and sent as v1; new captures are v2.
    [Fact]
    public async Task A_v1_capture_spooled_before_the_update_recovers_and_completes_as_v1()
    {
        using var scratch = new SourceScratch();
        await using (var queue = await scratch.Open())
        {
            Assert.True(queue.TryAppend(SourceScratch.Frame(0, "start", v: 1)));
            Assert.True(queue.TryAppend(SourceScratch.Frame(1, "tag", new { tag = "RESOURCES", value = 1 }, v: 1)));
            await queue.FlushAsync();
        }
        // A manifest written by the previous app version has no sourceVersion at all.
        string manifestPath = Directory.GetFiles(scratch.Path, "manifest.json", SearchOption.AllDirectories).Single();
        var old = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(manifestPath))!;
        Assert.True(old.Remove("sourceVersion"));
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(old));
        string chunk = Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        string before = await File.ReadAllTextAsync(chunk);

        await using var reopened = await scratch.Open();
        string after = await File.ReadAllTextAsync(chunk);
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.All(after.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("{\"v\":1,", line, StringComparison.Ordinal));
        using (var manifest = scratch.Manifest()) Assert.Equal(1, manifest.RootElement.GetProperty("sourceVersion").GetInt32());
        await using (var stream = File.OpenRead(chunk))
        {
            var parsed = await new SourceCaptureParser().ParseAsync(stream);
            Assert.Equal(1, parsed.SourceVersion);
        }

        var versions = new List<int>();
        using var http = new HttpClient(new Capture(versions));
        await reopened.SendPendingAsync(new(http));
        Assert.Equal(new[] { 1 }, versions);
    }

    [Fact]
    public async Task New_captures_are_spooled_and_completed_as_v2_and_a_version_switch_mid_capture_is_dropped()
    {
        using var scratch = new SourceScratch();
        await using var queue = await scratch.Open();
        SourceScratch.Complete(queue); await queue.FlushAsync();
        using (var manifest = scratch.Manifest()) Assert.Equal(2, manifest.RootElement.GetProperty("sourceVersion").GetInt32());
        var versions = new List<int>();
        using var http = new HttpClient(new Capture(versions));
        await queue.SendPendingAsync(new(http));
        Assert.Equal(new[] { 2 }, versions);

        using var mixed = new SourceScratch();
        await using var second = await mixed.Open();
        second.TryAppend(SourceScratch.Frame(0, "start"));
        second.TryAppend(SourceScratch.Frame(1, "tag", v: 1));
        await second.FlushAsync();
        Assert.Equal(1, second.State.Dropped);
        Assert.Empty(Directory.GetFiles(mixed.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    private sealed class Capture(List<int> versions) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            object reply = request.RequestUri!.AbsolutePath.EndsWith("chunks", StringComparison.Ordinal)
                ? new { ok = true, stored = true, duplicate = false, index = root.GetProperty("index").GetInt32(), sha256 = root.GetProperty("sha256").GetString() }
                : new { ok = true, status = "stored", captureId = SourceScratch.CaptureId };
            if (!request.RequestUri.AbsolutePath.EndsWith("chunks", StringComparison.Ordinal)) versions.Add(root.GetProperty("sourceVersion").GetInt32());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(reply)) };
        }
    }

    // late: the player switches capture on during turn 3 of the live game (the recorder writes late_attach).
    private static (List<SourceCaptureFrame> Frames, GameState Game) RecordFixture(bool end, bool late = false)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23)); var recorder = new SourceCaptureRecorder();
        var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add; recorder.SetEnabled(!late); parser.AddListener(recorder);
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame", "power.log.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line)
        {
            parser.Feed(line);
            if (late && !recorder.Enabled && parser.Game is { Turn: >= 3 }) recorder.SetEnabled(true);
        }
        parser.Flush();
        var game = parser.Game!;
        if (end)
        {
            // The fixture is cut before the game ends: a synthetic visible end on the local hero and player.
            game.LocalHero!.SetTag(GameTag.PlayerLeaderboardPlace, "3");
            game.LocalPlayerEntity!.SetTag(GameTag.PlayState, "LOST");
            recorder.OnGameEnded(game);
        }
        else recorder.Stop();
        return (frames, game);
    }

    private static JsonElement Data(SourceCaptureFrame frame) => SourceCaptureRecorderTests.Data(frame);

    private static async Task<SourceCaptureResult> Decode(IEnumerable<SourceCaptureFrame> frames)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', frames.Select(f => f.Record.ToJson())) + "\n"));
        return await new SourceCaptureParser().ParseAsync(stream);
    }

    private static async Task<SourceCaptureResult> Parse(string source)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        return await new SourceCaptureParser().ParseAsync(stream);
    }

    private static string Source(params string[] records) => string.Join('\n', records) + "\n";
    private static string Start(string mode, string kind = "game", string? format = null, string? arena = null, int v = 2) => v == 1
        ? Line(v, 0, "start", JsonSerializer.Serialize(new { captureKind = kind, mode, day = 20766, build = 123 }))
        : Line(v, 0, "start", JsonSerializer.Serialize(new { captureKind = kind, mode, day = 20766, build = 123, format, arena }));
    private static string Gap(int seq, string reason, int v = 2) => Line(v, seq, "gap", JsonSerializer.Serialize(new { reason, count = 1 }));
    private static string End(int seq, string? result = null, int? placement = null, string reason = "normal", int v = 2) =>
        Line(v, seq, "end", JsonSerializer.Serialize(new { status = "complete", result, placement, reason }));
    private static string Hero(int seq, bool resolved) => Line(2, seq, "choice", JsonSerializer.Serialize(new
    {
        choiceId = 1, sourceCard = (string?)null, options = new[] { "HERO_A", "HERO_B", "HERO_C", "HERO_D" },
        picked = resolved ? new[] { "HERO_B" } : [], resolved, choiceType = "mulligan",
    }), side: "local");
    private static string Line(int v, int seq, string kind, string data, string side = "none") =>
        JsonSerializer.Serialize(new { v, seq, kind, turn = 0, side, entity = 0, card = (string?)null, target = 0, data = JsonSerializer.Deserialize<JsonElement>(data) });
}
