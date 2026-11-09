using System.Globalization;
using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.SourceCapture.Processing;

namespace Hearthpulse.Tests;

/// <summary>Producer and decoder must agree on actual bytes, not merely pass separate schema examples.</summary>
public sealed class SourceCaptureIntegrationTests
{
    [Fact]
    public async Task Late_attach_and_unlinked_deck_observations_round_trip_through_decoder()
    {
        var (recorder, game, frames) = Begin("GT_RANKED", 4);
        var deck = SourceCaptureRecorderTests.Entity(game, 50, "OPAQUE_DECK_CARD", "DECK", 1, "MINION");
        recorder.OnEntityCreated(game, deck);
        recorder.OnEntityCreated(game, deck);
        recorder.Stop();
        var result = await Decode(frames);
        Assert.Equal(0, frames[0].Record.Turn);
        Assert.Equal("partial", result.Status);
        var deckRows = result.Events.Where(e => e.Kind == "entity" && e.Data.GetProperty("zone").GetString() == "DECK").ToArray();
        Assert.NotEmpty(deckRows);
        Assert.All(deckRows, e => { Assert.True(e.Entity > 0); Assert.Null(e.Card); });
        Assert.Equal(deckRows.Length, deckRows.Select(e => e.Entity).Distinct().Count());
    }

    [Fact]
    public async Task Visible_shop_updates_and_buys_decode_with_local_actor_and_preserved_item()
    {
        var (recorder, game, frames) = Begin("GT_BATTLEGROUNDS", 1);
        var item = SourceCaptureRecorderTests.Entity(game, 51, "SHOP_ITEM", "PLAY", 2, "MINION");
        recorder.OnEntityCreated(game, item);
        item.SetTag("ATK", "7");
        recorder.OnTagChanged(game, item, "ATK", "0", "7");
        var button = SourceCaptureRecorderTests.Entity(game, 52, "TB_BaconShop_DragBuy", "PLAY", 1, "SPELL");
        button.SetTag("CARD_TARGET", "51");
        recorder.OnEntityCreated(game, button);
        recorder.OnBlockStart(game, "PLAY", button);
        recorder.OnBlockEnd(game);
        recorder.Stop();
        var result = await Decode(frames);
        Assert.Equal(1, result.Summary.Buys);
        var buy = Assert.Single(result.Events, e => e.Kind == "buy");
        Assert.Equal("local", buy.Side);
        Assert.Equal("SHOP_ITEM", buy.Card);
        Assert.Contains(result.Events, e => e.Kind == "shop" && e.Card == "SHOP_ITEM" && e.Data.GetProperty("tags").TryGetProperty("ATK", out var attack) && attack.GetInt32() == 7);
    }

    [Fact]
    public async Task Public_opponent_blocks_and_tags_decode_without_redundant_hidden_identity()
    {
        var (recorder, game, frames) = Begin("GT_RANKED", 1);
        var enemy = SourceCaptureRecorderTests.Entity(game, 53, "PUBLIC_OPPONENT_CARD", "PLAY", 2, "MINION");
        recorder.OnEntityCreated(game, enemy);
        enemy.SetTag("ATK", "6");
        recorder.OnTagChanged(game, enemy, "ATK", "0", "6");
        recorder.OnBlockStart(game, "ATTACK", enemy);
        recorder.OnBlockEnd(game);
        recorder.Stop();
        var result = await Decode(frames);
        Assert.Contains(result.Events, e => e.Kind == "entity" && e.Card == "PUBLIC_OPPONENT_CARD");
        Assert.All(result.Events.Where(e => e.Side == "opponent" && e.Kind is "tag" or "block_start"), e => Assert.Null(e.Card));
    }

    [Fact]
    public async Task Rerolled_alternatives_and_incremental_multi_pick_resolution_preserve_history()
    {
        var (recorder, game, frames) = Begin("GT_RANKED", 1);
        var a = SourceCaptureRecorderTests.Entity(game, 54, "OPTION_A", "SETASIDE", 1, "MINION");
        var b = SourceCaptureRecorderTests.Entity(game, 55, "OPTION_B", "SETASIDE", 1, "MINION");
        recorder.OnChoices(game, "GENERAL", null, [a, b]);
        b.CardId = "OPTION_C";
        recorder.OnEntityRevealed(game, b);
        recorder.OnChoiceMade(game);
        recorder.OnChosen(game, b);
        recorder.OnChosen(game, a);
        recorder.Stop();
        var result = await Decode(frames);
        Assert.Equal(1, result.Summary.Choices);
        var choices = result.Events.Where(e => e.Kind == "choice").ToArray();
        Assert.Equal("OPTION_B", choices[0].Data.GetProperty("options")[1].GetString());
        Assert.Equal("OPTION_C", choices[^1].Data.GetProperty("options")[1].GetString());
        Assert.Equal(new[] { "OPTION_C", "OPTION_A" }, choices[^1].Data.GetProperty("picked").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Empty_offers_and_out_of_range_numeric_observations_remain_valid_partial_sources()
    {
        var (recorder, game, frames) = Begin("GT_RANKED", 1);
        var card = SourceCaptureRecorderTests.Entity(game, 56, "LOCAL_CARD", "HAND", 1, "MINION");
        card.SetTag("ATK", int.MaxValue.ToString(CultureInfo.InvariantCulture));
        recorder.OnEntityCreated(game, card);
        recorder.OnTagChanged(game, card, "ATK", "0", int.MaxValue.ToString(CultureInfo.InvariantCulture));
        recorder.OnChoices(game, "GENERAL", null, []);
        recorder.Stop();
        var result = await Decode(frames);
        Assert.Equal("partial", result.Status);
        Assert.True(result.Gaps > 0);
        Assert.DoesNotContain(result.Events, e => e.Kind == "choice");
        Assert.DoesNotContain(result.Events, e => e.Kind == "tag" && e.Data.GetProperty("value").GetInt32() == int.MaxValue);
    }

    [Fact]
    public async Task Crash_recovery_after_a_nonzero_turn_produces_parseable_partial_source()
    {
        using var scratch = new SourceScratch();
        await using (var queue = await scratch.Open())
        {
            Assert.True(queue.TryAppend(SourceScratch.Frame(0, "start")));
            Assert.True(queue.TryAppend(new(SourceScratch.CaptureId, "game", "ranked", 20766,
                new SourceCaptureRecord(1, "tag", 7, "none", 0, null, 0, new { tag = "TURN", value = 7 }))));
            await queue.FlushAsync();
        }
        await using var recovered = await SourceCaptureQueue.OpenAsync(scratch.Path);
        var path = Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        await using var source = File.OpenRead(path);
        var result = await new SourceCaptureParser().ParseAsync(source);
        Assert.Equal("partial", result.Status);
        Assert.Equal(new[] { 0, 7, 7, 7 }, result.Events.Select(e => e.Turn));
        Assert.False(result.Summary.CompleteForAggregation);
    }

    [Fact]
    public async Task Checked_in_battlegrounds_source_round_trips_from_recorder_to_decoder()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var recorder = new SourceCaptureRecorder();
        var frames = new List<SourceCaptureFrame>();
        recorder.Recorded += frames.Add; recorder.SetEnabled(true); parser.AddListener(recorder);
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame", "power.log.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line) parser.Feed(line);
        parser.Flush(); recorder.Stop();
        Assert.NotEmpty(frames);
        foreach (var frame in frames.Where(f => f.Record.Kind == "tag"))
        {
            var data = SourceCaptureRecorderTests.Data(frame);
            if (data.GetProperty("tag").GetString() == "TURN" && frame.Record.Side == "none" && frame.Record.Entity == 0)
                Assert.True(data.GetProperty("value").GetInt32() == frame.Record.Turn,
                    $"TURN seq {frame.Record.Seq}, side {frame.Record.Side}, ref {frame.Record.Entity}, value {data.GetProperty("value").GetInt32()}, turn {frame.Record.Turn}");
        }
        foreach (var capture in frames.GroupBy(f => f.CaptureId))
        {
            var result = await Decode(capture);
            Assert.Equal("partial", result.Status);
            Assert.True(result.Summary.Buys > 0);
        }
    }

    private static (SourceCaptureRecorder Recorder, GameState Game, List<SourceCaptureFrame> Frames) Begin(string mode, int turn)
    {
        var recorder = new SourceCaptureRecorder();
        var game = SourceCaptureRecorderTests.Game(mode);
        game.GameEntity!.SetTag("TURN", turn.ToString(CultureInfo.InvariantCulture));
        var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add;
        recorder.SetEnabled(true); recorder.OnGameStarted(game);
        recorder.OnEntityCreated(game, SourceCaptureRecorderTests.Entity(game, 49, "LOCAL_SEED", "HAND", 1, "MINION"));
        return (recorder, game, frames);
    }

    private static async Task<SourceCaptureResult> Decode(IEnumerable<SourceCaptureFrame> frames)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', frames.Select(f => f.Record.ToJson())) + "\n"));
        return await new SourceCaptureParser().ParseAsync(stream);
    }
}
