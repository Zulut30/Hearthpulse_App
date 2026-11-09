using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.SourceCapture.Processing;

namespace Hearthpulse.Tests;

/// <summary>Synthetic typed projections only; no personal logs, network, game client or app data.</summary>
public sealed class SourceCaptureParserTests : IDisposable
{
    private readonly string _folder = TestDataFolder.Scratch("hp-source-parser");
    public SourceCaptureParserTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => TestDataFolder.TryDelete(_folder);

    [Fact]
    public async Task Choice_actions_shop_resources_and_turns_preserve_order_and_local_counts()
    {
        string source = Source(
            Start(mode: "bg"),
            Line(1, "choice", """{"choiceId":7,"sourceCard":"DISC_SRC","options":["A","B","B","C"],"picked":[],"resolved":false}""", side: "local"),
            Line(2, "choice", """{"choiceId":7,"sourceCard":"DISC_SRC","options":["A","B","B","C"],"picked":["B"],"resolved":true}""", side: "local"),
            Line(3, "tag", """{"tag":"TURN","value":2}""", turn: 2),
            Line(4, "tag", """{"tag":"RESOURCES","value":8}""", turn: 2, side: "local"),
            Line(5, "entity", """{"zone":"SHOP","position":1,"tags":{"ATK":3,"HEALTH":4}}""", 2, "shop", 10, "B"),
            Line(6, "snapshot", """{"gold":8,"tier":3,"cards":[{"entity":10,"card":"B","side":"shop","zone":"SHOP","position":1,"tags":{"ATK":3}}]}""", 2, "local"),
            Action(7, "buy", 2), Action(8, "play", 2), Action(9, "sell", 2), Action(10, "refresh", 2),
            Line(11, "action", """{"action":"play","cost":null}""", 2, "opponent", 11, "VISIBLE"), End(12, turn: 2, placement: 3));
        var result = await Parse(source);

        Assert.Equal("parsed", result.Status);
        Assert.Equal(13, result.Records);
        Assert.Equal(0, result.Gaps);
        Assert.Equal(0, result.Unsupported);
        Assert.Equal(new[] { "start", "choice", "choice", "turn", "resources", "shop", "shop", "buy", "play", "sell", "refresh", "play", "end" }, result.Events.Select(x => x.Kind));
        Assert.Equal(Enumerable.Range(0, 13), result.Events.Select(x => x.Seq));
        Assert.Equal(new[] { "A", "B", "B", "C" }, result.Events[2].Data.GetProperty("options").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("B", result.Events[2].Data.GetProperty("picked")[0].GetString());
        Assert.Equal(7, result.Events[2].Data.GetProperty("choiceId").GetInt32());
        Assert.Equal(10, result.Events[5].Entity);
        Assert.Equal(3, result.Events[6].Data.GetProperty("tier").GetInt32());
        Assert.Equal(1, result.Summary.Plays);
        Assert.Equal(1, result.Summary.Buys);
        Assert.Equal(1, result.Summary.Sells);
        Assert.Equal(1, result.Summary.Choices);
        Assert.Equal(2, result.Summary.Turns);
        Assert.Equal(1, result.Summary.ShopSnapshots);
        Assert.Equal(1, result.Summary.Resources);
        Assert.True(result.Summary.CompleteForAggregation);
        Assert.Equal("W", result.Summary.Result);
    }

    [Fact]
    public async Task Reprocess_is_byte_stable_culture_independent_and_hashes_exact_source()
    {
        string source = Source(Start(), Action(1, "play"), End(2));
        var first = await Parse(source);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var again = await Parse(source);
            Assert.Equal(SourceWorkerCommand.Serialize(first), SourceWorkerCommand.Serialize(again));
        }
        finally { CultureInfo.CurrentCulture = original; }
        Assert.Equal("source-v1.2", first.ParserVersion);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), first.SourceSha256);
        var reordered = await Parse(source.Replace("\"v\":1,\"seq\":0", "\"seq\":0,\"v\":1", StringComparison.Ordinal));
        Assert.NotEqual(first.SourceSha256, reordered.SourceSha256);
        Assert.Equal(JsonSerializer.Serialize(first.Events), JsonSerializer.Serialize(reordered.Events));
        Assert.Equal(first.Summary, reordered.Summary);
    }

    [Fact]
    public async Task Fragmented_reads_preserve_hash_record_boundaries_and_leave_input_open()
    {
        string source = Source(Start(), Action(1, "buy"), End(2));
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(source));
        var result = await new SourceCaptureParser().ParseAsync(stream);
        Assert.Equal(3, result.Records);
        Assert.Equal(1, result.Summary.Buys);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), result.SourceSha256);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)]
    public async Task Malformed_envelopes_sequences_and_boundaries_fail_closed(int scenario)
    {
        string valid = Source(Start(), End(1));
        string source = scenario switch
        {
            0 => "",
            1 => valid.TrimEnd('\n'),
            2 => "\ufeff" + valid,
            3 => valid.Replace("\n", "\r\n", StringComparison.Ordinal),
            4 => valid + "\n",
            5 => Source(End(0)),
            6 => Source(Start(), End(2)),
            7 => Source(Start(), End(0)),
            8 => Source(Start(), Start(seq: 1), End(2)),
            9 => Source(Start(), End(1), Action(2, "play")),
            10 => Source(Start()),
            11 => valid.Replace("\"v\":1", "\"v\":1,\"v\":1", StringComparison.Ordinal),
            12 => valid.Replace("\"v\":1", "\"v\":2", StringComparison.Ordinal),
            13 => valid.Replace("\"turn\":0", "\"turn\":1001", StringComparison.Ordinal),
            14 => valid.Replace("\"entity\":0", "\"entity\":1000001", StringComparison.Ordinal),
            15 => valid.Replace("\"seq\":0", "\"seq\":0.5", StringComparison.Ordinal),
            16 => Source(Start(), Action(1, "play", 2), End(2, turn: 1)),
            17 => Source(Start(), Line(1, "block_end", """{"block":"PLAY"}"""), End(2)),
            _ => valid.Replace("\"v\":1", "\"v\": 1", StringComparison.Ordinal),
        };
        var error = await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(source));
        Assert.Equal(scenario == 12 ? "unsupported_version" : "invalid_source", error.Code);
        Assert.Equal(error.Code, error.Message);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)]
    public async Task Private_fields_hidden_cards_and_reference_links_are_rejected(int scenario)
    {
        string[] observations = scenario switch
        {
            0 => [Line(1, "entity", """{"zone":"HAND","position":1,"tags":{}}""", side: "opponent", entity: 1, card: "SECRET_CARD")],
            1 => [Line(1, "snapshot", """{"gold":null,"tier":null,"cards":[{"entity":1,"card":"SECRET_CARD","side":"opponent","zone":"DECK","position":0,"tags":{}}]}""")],
            2 => [Line(1, "tag", """{"tag":"CREATOR_DBID","value":100}""", side: "local")],
            3 => [Line(1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A"],"picked":["A"],"resolved":true}""", side: "opponent")],
            4 => [Line(1, "action", """{"action":"draw","cost":null}""", side: "opponent", card: "SECRET_CARD")],
            5 => [Line(1, "future", """{"PlayerName":"private-sentinel#1234"}""")],
            6 => [Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{"HEALTH":"private-sentinel"}}""", side: "local", entity: 1)],
            7 => [Line(1, "action", """{"action":"play","cost":null,"path":"C:/private-sentinel"}""", side: "local")],
            8 => [Entity(1, "PLAY", "VISIBLE"), Entity(2, "DECK", null)],
            9 => [Entity(1, "HAND", null), Entity(2, "PLAY", "SECRET_CARD")],
            10 => [Entity(1, "HAND", null), Line(2, "tag", """{"tag":"COST","value":5}""", side: "opponent", entity: 42)],
            11 => [Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{}}""", side: "local", entity: 1, card: "private-sentinel#1234")],
            12 => [Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{}}""", side: "local", entity: 42, card: "VISIBLE"), Entity(2, "HAND", null)],
            _ => [Entity(1, "HAND", null), Line(2, "entity", """{"zone":"PLAY","position":1,"tags":{}}""", side: "local", entity: 42, card: "SECRET_CARD")],
        };
        var error = await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(Source([Start(), .. observations, End(observations.Length + 1)])));
        Assert.Equal("invalid_source", error.Code);
        Assert.DoesNotContain("private-sentinel", error.ToString());
        Assert.DoesNotContain("SECRET_CARD", error.ToString());
    }

    [Fact]
    public async Task Hidden_structure_and_fresh_public_reference_do_not_restore_hidden_identity()
    {
        var result = await Parse(Source(Start(), Entity(1, "HAND", null),
            Line(2, "entity", """{"zone":"PLAY","position":1,"tags":{"ATK":3}}""", side: "opponent", entity: 43, card: "VISIBLE"), End(3)));
        Assert.Null(result.Events[1].Card);
        Assert.Equal(42, result.Events[1].Entity);
        Assert.Equal(43, result.Events[2].Entity);
        Assert.Equal("VISIBLE", result.Events[2].Card);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task Gaps_unsupported_and_unresolved_sources_never_claim_complete_aggregation(int scenario)
    {
        string observation = scenario switch
        {
            0 => Line(1, "gap", """{"reason":"rotation","count":3}"""),
            1 => Line(1, "gap", """{"reason":"unsupported","count":1}"""),
            2 => Action(1, "unknown"),
            3 => Line(1, "future_observation", "{}"),
            4 => Line(1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A"],"picked":[],"resolved":false}""", side: "local"),
            _ => Line(1, "block_start", """{"block":"OTHER"}"""),
        };
        var result = await Parse(Source(Start(), observation, End(2)));
        Assert.Equal("partial", result.Status);
        Assert.Equal("incomplete", result.Summary.CaptureStatus);
        Assert.False(result.Summary.CompleteForAggregation);
        Assert.Null(result.Summary.Result);
        Assert.Null(result.Summary.Placement);
        Assert.Equal(0, result.Summary.Plays);
        Assert.Equal(scenario is 1 or 2 or 3 or 5 ? 1 : 0, result.Unsupported);
        Assert.Equal(scenario switch { 0 => 3, 4 => 0, 5 => 2, _ => 1 }, result.Gaps);
        if (scenario == 3) Assert.Equal("unsupported", result.Events[1].Kind);
    }

    [Fact]
    public async Task Terminal_gap_and_incomplete_end_preserve_observations_without_inventing_result()
    {
        var torn = await Parse(Source(Start(), Action(1, "play"), Line(2, "gap", """{"reason":"shutdown","count":1}""")));
        Assert.False(torn.Summary.HasEnd);
        Assert.Equal(1, torn.Summary.Plays);
        Assert.Null(torn.Summary.Result);
        var incomplete = await Parse(Source(Start(), End(1, status: "incomplete")));
        Assert.True(incomplete.Summary.HasEnd);
        Assert.Equal("partial", incomplete.Status);
        Assert.Null(incomplete.Summary.Result);
    }

    [Fact]
    public async Task Draft_packages_and_selected_index_are_preserved_without_game_result()
    {
        string choice = """{"pick":0,"options":[{"card":"A","package":["X","X"]},{"card":"B","package":["Y"]}],"picked":1,"underground":true}""";
        var result = await Parse(Source(Start(kind: "draft", mode: "arena"), Line(1, "draft_choice", choice, side: "local"), End(2, result: null)));
        Assert.Equal("parsed", result.Status);
        Assert.Equal(1, result.Summary.DraftChoices);
        Assert.Equal(1, result.Events[1].Data.GetProperty("picked").GetInt32());
        Assert.Equal(new[] { "X", "X" }, result.Events[1].Data.GetProperty("options")[0].GetProperty("package").EnumerateArray().Select(x => x.GetString()));
        Assert.Null(result.Summary.Result);
        Assert.False(result.Summary.CompleteForAggregation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Invalid_choice_membership_duplicates_and_draft_indices_are_rejected(int scenario)
    {
        string line = scenario switch
        {
            0 => Line(1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A","B"],"picked":["C"],"resolved":true}""", side: "local"),
            1 => Line(1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A","B"],"picked":["B","B"],"resolved":true}""", side: "local"),
            2 => Line(1, "choice", """{"choiceId":1,"sourceCard":null,"options":["A"],"picked":["A"],"resolved":false}""", side: "local"),
            3 => Line(1, "draft_choice", """{"pick":0,"options":[{"card":"A","package":[]}],"picked":1,"underground":null}""", side: "local"),
            _ => Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{"ATK":1,"ATK":2}}""", side: "local", entity: 1),
        };
        await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(Source(Start(kind: "draft", mode: "arena"), line, End(2, result: null))));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Source_line_and_record_budgets_accept_exact_limit_and_reject_next_byte_or_record(int budget)
    {
        string source = Source(Start(), End(1));
        int bytes = Encoding.UTF8.GetByteCount(source);
        int longest = source.Split('\n').Max(x => Encoding.UTF8.GetByteCount(x));
        var exact = budget switch
        {
            0 => new SourceCaptureLimits(MaxSourceBytes: bytes),
            1 => new SourceCaptureLimits(MaxLineBytes: longest),
            _ => new SourceCaptureLimits(MaxRecords: 2),
        };
        Assert.Equal("parsed", (await Parse(source, exact)).Status);
        var below = budget switch
        {
            0 => exact with { MaxSourceBytes = bytes - 1 },
            1 => exact with { MaxLineBytes = longest - 1 },
            _ => exact with { MaxRecords = 1 },
        };
        Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(source, below))).Code);
    }

    [Fact]
    public async Task Oversized_line_derived_budget_and_invalid_utf8_fail_without_partial_result()
    {
        await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(new string('x', 65_537) + "\n"));
        Assert.Equal("limit_exceeded", (await Assert.ThrowsAsync<SourceCaptureException>(() =>
            Parse(Source(Start(), End(1)), new SourceCaptureLimits(MaxDerivedBytes: 1)))).Code);
        using var invalid = new MemoryStream([0xff, (byte)'\n']);
        Assert.Equal("invalid_source", (await Assert.ThrowsAsync<SourceCaptureException>(() => new SourceCaptureParser().ParseAsync(invalid))).Code);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task Contract_collection_and_identifier_limits_accept_boundary_and_reject_one_more(int scenario)
    {
        string Observation(int extra) => scenario switch
        {
            0 => Line(1, "choice", JsonSerializer.Serialize(new { choiceId = 1, sourceCard = (string?)null,
                options = Enumerable.Repeat("A", 64 + extra), picked = new[] { "A" }, resolved = true }), side: "local"),
            1 => Line(1, "choice", JsonSerializer.Serialize(new { choiceId = 1, sourceCard = (string?)null,
                options = Enumerable.Repeat("A", 32), picked = Enumerable.Repeat("A", 16 + extra), resolved = true }), side: "local"),
            2 => Line(1, "snapshot", JsonSerializer.Serialize(new { gold = (int?)null, tier = (int?)null,
                cards = Enumerable.Range(1, 256 + extra).Select(i => new { entity = i, card = "A", side = "local", zone = "PLAY", position = i, tags = new { } }) })),
            3 => Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{}}""", side: "local", entity: 1, card: new string('A', 80 + extra)),
            4 => Line(1, "entity", """{"zone":"PLAY","position":1,"tags":{}}""", side: "local", entity: 1_000_000 + extra),
            _ => Line(1, "tag", JsonSerializer.Serialize(new { tag = "TURN", value = 1_000 + extra }), turn: 1_000 + extra),
        };
        int turn = scenario == 5 ? 1_000 : 0;
        Assert.Equal("parsed", (await Parse(Source(Start(), Observation(0), End(2, turn)))).Status);
        Assert.Equal("invalid_source", (await Assert.ThrowsAsync<SourceCaptureException>(() => Parse(Source(Start(), Observation(1), End(2, turn))))).Code);
    }

    [Fact]
    public async Task Public_blocks_and_supported_actions_are_observations_not_inferred_extra_plays()
    {
        var result = await Parse(Source(Start(),
            Line(1, "block_start", """{"block":"PLAY"}""", side: "local", entity: 12, card: "CARD_1"),
            Action(2, "play"), Line(3, "block_end", """{"block":"PLAY"}"""), Action(4, "freeze"),
            Action(5, "upgrade"), Action(6, "move"), Action(7, "hero_power"), Action(8, "receive"),
            Action(9, "draw"), Action(10, "discard"), End(11)));
        Assert.Equal("parsed", result.Status);
        Assert.Equal(1, result.Summary.Plays);
        Assert.Equal(new[] { "block_start", "play", "block_end", "freeze", "upgrade", "move", "hero_power", "receive", "draw", "discard" },
            result.Events.Skip(1).Take(10).Select(x => x.Kind));
        Assert.Equal(JsonValueKind.Null, result.Events[2].Data.GetProperty("cost").ValueKind);
    }

    [Fact]
    public async Task Repeated_resolved_choice_is_counted_once_and_conflicting_resolution_is_invalid()
    {
        string Choice(int seq, string picked) => Line(seq, "choice", JsonSerializer.Serialize(new { choiceId = 1,
            sourceCard = (string?)null, options = new[] { "A", "B" }, picked = new[] { picked }, resolved = true }), side: "local");
        var result = await Parse(Source(Start(), Choice(1, "B"), Choice(2, "B"), End(3)));
        Assert.Equal(1, result.Summary.Choices);
        Assert.Equal(4, result.Records);
        Assert.Equal("invalid_source", (await Assert.ThrowsAsync<SourceCaptureException>(() =>
            Parse(Source(Start(), Choice(1, "B"), Choice(2, "A"), End(3))))).Code);
    }

    [Fact]
    public async Task A_gap_explains_missing_block_boundaries_and_empty_snapshot_does_not_invent_shop_coverage()
    {
        var result = await Parse(Source(Start(mode: "bg"), Line(1, "gap", """{"reason":"late_attach","count":1}"""),
            Line(2, "block_end", """{"block":"POWER"}"""),
            Line(3, "snapshot", """{"gold":null,"tier":null,"cards":[]}""", side: "local"), End(4)));
        Assert.Equal("partial", result.Status);
        Assert.Equal(2, result.Gaps);
        Assert.Equal(0, result.Summary.ShopSnapshots);
        Assert.Equal("snapshot", result.Events[3].Kind);
    }

    [Fact]
    public async Task Cli_repeats_atomically_and_recovers_after_invalid_input_or_failed_output()
    {
        string input = Path.Combine(_folder, "private-sentinel.jsonl"), output = Path.Combine(_folder, "result.json");
        await File.WriteAllTextAsync(input, Source(Start(), Action(1, "buy"), End(2)), new UTF8Encoding(false));
        using var status = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--input", input, "--output", output], status));
        string saved = await File.ReadAllTextAsync(output);
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("buys").GetInt32());
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--output", output, "--input", input], status));
        Assert.Equal(saved, await File.ReadAllTextAsync(output));
        await File.WriteAllTextAsync(input, "private-sentinel#1234\n");
        Assert.Equal(2, await SourceWorkerCommand.RunAsync(["--input", input, "--output", output], status));
        Assert.Equal(saved, await File.ReadAllTextAsync(output));
        await File.WriteAllTextAsync(input, Source(Start(), End(1)), new UTF8Encoding(false));
        string blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);
        Assert.Equal(4, await SourceWorkerCommand.RunAsync(["--input", input, "--output", blocked], status));
        Assert.Empty(Directory.GetFiles(_folder, ".source-worker-*.tmp"));
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--input", input, "--output", output], status));
        using var recovered = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal(0, recovered.RootElement.GetProperty("summary").GetProperty("buys").GetInt32());
        Assert.DoesNotContain("private-sentinel", status.ToString());
        Assert.DoesNotContain(_folder, status.ToString());
        Assert.Equal(5, status.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        foreach (string line in status.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var safe = JsonDocument.Parse(line);
            Assert.False(safe.RootElement.TryGetProperty("events", out _));
            Assert.False(safe.RootElement.TryGetProperty("sourceSha256", out _));
        }
    }

    [Fact]
    public async Task Cli_rejects_duplicate_arguments_same_file_missing_input_and_cancellation_safely()
    {
        string input = Path.Combine(_folder, "private-sentinel.jsonl");
        string valid = Source(Start(), End(1));
        await File.WriteAllTextAsync(input, valid, new UTF8Encoding(false));
        using var status = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(2, await SourceWorkerCommand.RunAsync(["--input", input, "--input", input], status));
        Assert.Equal(2, await SourceWorkerCommand.RunAsync(["--input", input, "--output", input], status));
        Assert.Equal(valid, await File.ReadAllTextAsync(input));
        Assert.Equal(4, await SourceWorkerCommand.RunAsync(["--input", input + ".missing", "--output", input + ".out"], status));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(3, await SourceWorkerCommand.RunAsync(["--input", input, "--output", input + ".out"], status, cancelled.Token));
        Assert.False(File.Exists(input + ".out"));
        Assert.DoesNotContain("private-sentinel", status.ToString());
    }

    [Fact]
    public async Task Cli_partial_source_returns_zero_and_only_safe_technical_status()
    {
        string input = Path.Combine(_folder, "source.jsonl"), output = Path.Combine(_folder, "result.json");
        await File.WriteAllTextAsync(input, Source(Start(), Line(1, "gap", """{"reason":"unsupported","count":2}"""), End(2)), new UTF8Encoding(false));
        using var status = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await SourceWorkerCommand.RunAsync(["--input", input, "--output", output], status));
        Assert.Equal("""{"ok":true,"status":"partial","parserVersion":"source-v1.2","records":3,"unsupported":1,"gaps":2}""" + Environment.NewLine, status.ToString());
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal("incomplete", json.RootElement.GetProperty("summary").GetProperty("captureStatus").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("summary").GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_failed_job_does_not_poison_reuse_of_the_decoder()
    {
        var parser = new SourceCaptureParser();
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes(Source(Start(), Action(1, "play"), End(3))));
        await Assert.ThrowsAsync<SourceCaptureException>(() => parser.ParseAsync(invalid));
        using var next = new MemoryStream(Encoding.UTF8.GetBytes(Source(Start(), Action(1, "buy"), End(2))));
        var result = await parser.ParseAsync(next);
        Assert.Equal(0, result.Summary.Plays);
        Assert.Equal(1, result.Summary.Buys);
        Assert.Equal(3, result.Records);
        Assert.Equal("parsed", result.Status);
    }

    [Fact]
    public async Task A_complete_ranked_capture_without_result_is_not_eligible_for_completed_game_aggregates()
    {
        var parsed = await Parse(Source(Start(), Action(1, "play"), End(2, result: null)));
        Assert.Equal("parsed", parsed.Status);
        Assert.Equal("complete", parsed.Summary.CaptureStatus);
        Assert.Equal(1, parsed.Summary.Plays);
        Assert.Null(parsed.Summary.Result);
        Assert.False(parsed.Summary.CompleteForAggregation);
    }

    [Fact]
    public async Task A_local_turn_counter_does_not_replace_the_global_game_turn()
    {
        var parsed = await Parse(Source(Start(),
            Line(1, "tag", """{"tag":"TURN","value":3}""", turn: 3),
            Line(2, "tag", """{"tag":"TURN","value":2}""", turn: 3, side: "local"), End(3, turn: 3)));
        Assert.Equal(3, parsed.Summary.Turns);
        Assert.Equal("turn", parsed.Events[1].Kind);
        Assert.Equal("tag", parsed.Events[2].Kind);
        Assert.Equal(2, parsed.Events[2].Data.GetProperty("value").GetInt32());
        Assert.True(parsed.Summary.CompleteForAggregation);
    }

    [Fact]
    public async Task A_complete_bg_capture_without_placement_is_not_eligible_for_completed_game_aggregates()
    {
        var parsed = await Parse(Source(Start(mode: "bg"), End(1)));
        Assert.Equal("parsed", parsed.Status);
        Assert.Equal("complete", parsed.Summary.CaptureStatus);
        Assert.Null(parsed.Summary.Placement);
        Assert.False(parsed.Summary.CompleteForAggregation);
    }

    [Theory]
    [InlineData("ranked", "W", null, true)]
    [InlineData("casual", "L", null, true)]
    [InlineData("arena", "T", null, true)]
    [InlineData("brawl", "W", null, true)]
    [InlineData("casual", null, 1, false)]
    [InlineData("bg", null, 1, true)]
    [InlineData("bg", null, 8, true)]
    [InlineData("duos", null, 1, true)]
    [InlineData("duos", null, 4, true)]
    [InlineData("duos", "W", 5, false)]
    [InlineData("duos", "W", 8, false)]
    public async Task Aggregate_eligibility_requires_the_outcome_of_the_specific_mode(string mode, string? result, int? placement, bool eligible)
    {
        var parsed = await Parse(Source(Start(mode: mode), End(1, result: result, placement: placement)));
        Assert.Equal("parsed", parsed.Status);
        Assert.Equal(result, parsed.Summary.Result);
        Assert.Equal(placement, parsed.Summary.Placement);
        Assert.Equal(eligible, parsed.Summary.CompleteForAggregation);
    }

    private static async Task<SourceCaptureResult> Parse(string source, SourceCaptureLimits? limits = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        return await new SourceCaptureParser(limits).ParseAsync(stream);
    }

    private static string Source(params string[] records) => string.Join('\n', records) + (records.Length == 0 ? "" : "\n");
    private static string Start(int seq = 0, string kind = "game", string mode = "ranked") =>
        Line(seq, "start", JsonSerializer.Serialize(new { captureKind = kind, mode, day = 20766, build = (int?)null }));
    private static string End(int seq, int turn = 0, string status = "complete", string? result = "W", int? placement = null) =>
        Line(seq, "end", JsonSerializer.Serialize(new { status, result, placement, reason = "normal" }), turn);
    private static string Action(int seq, string action, int turn = 0) =>
        Line(seq, "action", JsonSerializer.Serialize(new { action, cost = (int?)null }), turn, "local", 12, "CARD_1");
    private static string Entity(int seq, string zone, string? card) =>
        Line(seq, "entity", JsonSerializer.Serialize(new { zone, position = 1, tags = new { } }), side: "opponent", entity: 42, card: card);
    private static string Line(int seq, string kind, string data, int turn = 0, string side = "none", int entity = 0, string? card = null) =>
        JsonSerializer.Serialize(new { v = 1, seq, kind, turn, side, entity, card, target = 0, data = JsonSerializer.Deserialize<JsonElement>(data) });

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
    }
}
