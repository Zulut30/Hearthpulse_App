using System.IO.Compression;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

public sealed class SourceCaptureRecorderTests
{
    [Fact]
    public void Session_registers_once_suppresses_catchup_and_seals_the_live_capture_on_stop()
    {
        using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false, IsHearthstoneRunning = () => false };
        var frames = new List<SourceCaptureFrame>(); session.SourceCaptureRecorded += frames.Add; session.SetSourceCaptureEnabled(true);
        session.OnSessionStarted("synthetic", new(2026, 11, 9), attachedMidSession: true);
        session.OnLines("Power.log", ReplayRecorderTests.Setup.Select(b => "D 12:30:00.0000000 GameState." + b).ToArray(), catchingUp: true);
        Assert.Empty(frames);
        session.OnLines("Power.log", ["D 12:30:01.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=6"], catchingUp: false);
        session.OnLines("Power.log", ["D 12:30:01.0000000 GameState.DebugPrintPower() - FUTURE_STRUCTURE C:\\Users\\private#1234"], catchingUp: false);
        session.StopSourceCapture();
        Assert.Single(frames, f => f.Record.Kind == "start"); Assert.Single(frames, f => f.Record.Kind == "end");
        Assert.Contains(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "late_attach");
        Assert.Contains(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "unsupported");
        Assert.DoesNotContain("private", string.Join('\n', frames.Select(f => f.Record.ToJson())));
        Assert.Equal("incomplete", Data(frames.Last()).GetProperty("status").GetString());
    }

    [Fact]
    public void Arena_session_captures_pick_and_unpicked_close_even_with_the_helper_off_and_no_game()
    {
        using var memory = new HearthstoneMemory();
        var session = new GameSession(HistoryStore.InMemory(), memory) { ReadsMemory = false, IsHearthstoneRunning = () => false, Clock = () => new(2026, 11, 9) };
        var frames = new List<SourceCaptureFrame>(); session.SourceCaptureRecorded += frames.Add; session.SetSourceCaptureEnabled(true);
        session.OnLines("LoadingScreen.log", ["D 12:30:00.0000000 LoadingScreen.OnSceneLoaded() - prevMode=HUB currMode=DRAFT"], false);
        session.SetDraftOffer(["DRAFT_A", "DRAFT_B", "DRAFT_C"]);
        session.OnLines("Arena.log", ["D 12:30:01.0000000 Client chooses: Card (DRAFT_B)"], false);
        session.SetDraftOffer(["NEXT_A", "NEXT_B", "NEXT_C"]);
        session.OnLines("LoadingScreen.log", ["D 12:30:02.0000000 LoadingScreen.OnSceneLoaded() - prevMode=DRAFT currMode=HUB"], false);
        session.SetDraftOffer((ArenaDraftOffer?)null); // Memory clearing after the scene close must not duplicate it.
        var choices = frames.Where(f => f.Record.Kind == "draft_choice").ToArray();
        Assert.Equal(2, choices.Length); Assert.Equal(1, Data(choices[0]).GetProperty("picked").GetInt32());
        Assert.Equal(JsonValueKind.Null, Data(choices[1]).GetProperty("picked").ValueKind);
        Assert.All(frames, f => Assert.Equal("draft", f.CaptureKind));
    }

    [Fact]
    public void Hidden_cards_names_account_ids_paths_and_post_game_reveals_never_enter_source()
    {
        var (recorder, parser, frames) = Start();
        ReplayRecorderTests.Feed(parser,
        [
            .. ReplayRecorderTests.Card(800, "SECRET_CARD", "DECK", 2, "MINION", "tag=CREATOR_DBID value=424242", "tag=ATK value=99"),
            ReplayRecorderTests.Zone(800, "HAND", "SECRET_CARD"),
            .. ReplayRecorderTests.Show(800, "SECRET_CARD", "tag=COST value=10"),
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=FINAL_GAMEOVER",
            .. ReplayRecorderTests.Card(801, "FUTURE_CARD", "PLAY", 2, "MINION"),
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=PLAYSTATE value=WON",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE",
        ]);
        parser.Flush();
        var text = string.Join('\n', frames.Select(f => f.Record.ToJson()));
        foreach (var forbidden in new[] { "SECRET_CARD", "FUTURE_CARD", "velox", "21276", "GameAccountId", "144115198130930503", "CREATOR", "424242", "C:\\Users" }) Assert.DoesNotContain(forbidden, text);
        var back = frames.Last(f => f.Record.Kind == "entity" && f.Record.Side == "opponent" && Data(f).GetProperty("zone").GetString() == "HAND");
        Assert.Null(back.Record.Card);
        Assert.Empty(Data(back).GetProperty("tags").EnumerateObject());
        Assert.Equal("W", Data(frames.Last()).GetProperty("result").GetString());
        Assert.Null(recorder.CaptureId);
    }

    [Fact]
    public void Conceal_shuffle_and_setaside_break_refs_and_never_restore_a_stale_card()
    {
        var (_, parser, frames) = Start();
        ReplayRecorderTests.Feed(parser,
        [
            .. ReplayRecorderTests.Card(802, "PUBLIC_A", "PLAY", 2, "MINION"),
            "DebugPrintPower() - HIDE_ENTITY - Entity=[entityName=X id=802 zone=PLAY zonePos=1 cardId=PUBLIC_A player=2] tag=ZONE value=DECK",
            ReplayRecorderTests.Zone(802, "SETASIDE", "PUBLIC_A"),
            ReplayRecorderTests.Zone(802, "HAND", "PUBLIC_A"),
            "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=[entityName=X id=802 zone=HAND zonePos=1 cardId=PUBLIC_A player=2] CardID=HIDDEN_B",
            ReplayRecorderTests.Zone(802, "PLAY", "HIDDEN_B"),
            .. ReplayRecorderTests.Show(802, "REVEALED_C", "tag=CARDTYPE value=MINION"),
        ]);
        parser.Flush();
        var shown = frames.Where(f => f.Record.Card == "PUBLIC_A").ToArray();
        Assert.Single(shown);
        var back = frames.Single(f => f.Record.Kind == "entity" && Data(f).GetProperty("zone").GetString() == "HAND" && f.Record.Side == "opponent");
        var revealed = frames.Last(f => f.Record.Card == "REVEALED_C");
        Assert.NotEqual(shown[0].Record.Entity, back.Record.Entity);
        Assert.NotEqual(back.Record.Entity, revealed.Record.Entity);
        Assert.DoesNotContain(frames, f => f.Record.Card == "HIDDEN_B");
        var deckRows = frames.Where(f => f.Record.Kind == "entity" && Data(f).GetProperty("zone").GetString() == "DECK").ToArray();
        Assert.All(deckRows, f => { Assert.True(f.Record.Entity > 0); Assert.Null(f.Record.Card); });
        Assert.Equal(deckRows.Length, deckRows.Select(f => f.Record.Entity).Distinct().Count());
    }

    [Fact]
    public void Choice_alternatives_duplicates_picked_and_nested_block_order_survive_projection()
    {
        var (recorder, parser, frames) = Start();
        var game = parser.Game!;
        var a = game.GetOrCreate(810); a.CardId = "OPTION_A";
        var b = game.GetOrCreate(811); b.CardId = "OPTION_B";
        recorder.OnBlockStart(game, "POWER", game.Entities[99]);
        recorder.OnChoices(game, "GENERAL", game.Entities[99], [a, b, a]);
        recorder.OnChoiceMade(game); recorder.OnChosen(game, b); recorder.OnBlockEnd(game);
        var choice = frames.Last(f => f.Record.Kind == "choice");
        Assert.Equal(new[] { "OPTION_A", "OPTION_B", "OPTION_A" }, Data(choice).GetProperty("options").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { "OPTION_B" }, Data(choice).GetProperty("picked").EnumerateArray().Select(x => x.GetString()));
        Assert.True(Data(choice).GetProperty("resolved").GetBoolean());
        Assert.Equal(new[] { "block_start", "choice", "choice", "choice", "block_end" }, frames.TakeLast(5).Select(f => f.Record.Kind));
        Assert.Equal(Enumerable.Range(0, frames.Count), frames.Select(f => f.Record.Seq));
        Assert.DoesNotContain(frames, f => f.Record.Entity is 810 or 811);
    }

    [Fact]
    public void Visible_reroll_updates_choice_alternatives_without_mutating_earlier_observations()
    {
        var (recorder, parser, frames) = Start(); var game = parser.Game!;
        var a = Entity(game, 830, "HERO_A", "SETASIDE", 1, "HERO"); var b = Entity(game, 831, "HERO_B", "SETASIDE", 1, "HERO");
        recorder.OnChoices(game, "GENERAL", null, [a, b]);
        var original = frames.Last(f => f.Record.Kind == "choice");
        b.CardId = "HERO_C"; recorder.OnEntityRevealed(game, b); recorder.OnChosen(game, b);
        Assert.Equal(new[] { "HERO_A", "HERO_B" }, Data(original).GetProperty("options").EnumerateArray().Select(p => p.GetString()));
        var selected = frames.Last(f => f.Record.Kind == "choice");
        Assert.Equal(new[] { "HERO_A", "HERO_C" }, Data(selected).GetProperty("options").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(new[] { "HERO_C" }, Data(selected).GetProperty("picked").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void Resolved_choice_never_follows_an_option_into_hidden_opponent_zones()
    {
        var (recorder, parser, frames) = Start(); var game = parser.Game!;
        var option = Entity(game, 835, "VISIBLE_OPTION", "SETASIDE", 1, "MINION");
        recorder.OnChoices(game, "GENERAL", null, [option]); recorder.OnChoiceMade(game); recorder.OnChosen(game, option);
        option.SetTag("CONTROLLER", "2"); option.SetTag("ZONE", "DECK"); recorder.OnTagChanged(game, option, "ZONE", "SETASIDE", "DECK");
        recorder.OnEntityHidden(game, option); option.CardId = "HIDDEN_FUTURE_OPTION"; recorder.OnEntityRevealed(game, option);
        Assert.Equal(3, frames.Count(f => f.Record.Kind == "choice"));
        Assert.DoesNotContain("HIDDEN_FUTURE_OPTION", string.Join('\n', frames.Select(f => f.Record.ToJson())));
    }

    [Fact]
    public void Buy_target_arriving_inside_the_block_keeps_the_visible_card_context_once()
    {
        var game = Game("GT_BATTLEGROUNDS"); var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add; recorder.SetEnabled(true); recorder.OnGameStarted(game);
        var shop = Entity(game, 840, "OFFERED_CARD", "PLAY", 2, "MINION"); recorder.OnEntityCreated(game, shop);
        var button = Entity(game, 841, "TB_BaconShop_DragBuy", "PLAY", 1, "MOVE_MINION_HOVER_TARGET");
        recorder.OnBlockStart(game, "PLAY", button); button.SetTag("CARD_TARGET", "840"); recorder.OnTagChanged(game, button, "CARD_TARGET", "0", "840"); recorder.OnBlockEnd(game);
        var action = Assert.Single(frames, f => f.Record.Kind == "action");
        Assert.Equal("buy", Data(action).GetProperty("action").GetString()); Assert.Equal("OFFERED_CARD", action.Record.Card);
        Assert.True(action.Record.Entity > 0); Assert.Equal(action.Record.Entity, action.Record.Target);
    }

    [Fact]
    public void Visible_shop_buy_sell_play_refresh_and_snapshot_are_ordered_with_unknown_cost()
    {
        var game = Game("GT_BATTLEGROUNDS");
        var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>();
        recorder.Recorded += frames.Add; recorder.SetEnabled(true); recorder.OnGameStarted(game);
        var shop = Entity(game, 900, "SHOP_MINION", "PLAY", 2, "MINION"); shop.SetTag("ATK", "5"); recorder.OnEntityCreated(game, shop);
        var button = Entity(game, 901, "TB_BaconShop_DragBuy", "PLAY", 1, "MOVE_MINION_HOVER_TARGET"); button.SetTag("CARD_TARGET", "900");
        recorder.OnBlockStart(game, "PLAY", button); recorder.OnBlockEnd(game);
        shop.SetTag("ATK", "7"); recorder.OnTagChanged(game, shop, "ATK", "5", "7");
        shop.SetTag("CONTROLLER", "1"); recorder.OnTagChanged(game, shop, "CONTROLLER", "2", "1");
        shop.SetTag("ZONE", "HAND"); recorder.OnTagChanged(game, shop, "ZONE", "PLAY", "HAND");
        recorder.OnBlockStart(game, "PLAY", shop); shop.SetTag("ZONE", "PLAY"); recorder.OnTagChanged(game, shop, "ZONE", "HAND", "PLAY"); recorder.OnBlockEnd(game);
        button.CardId = "TB_BaconShop_DragSell"; recorder.OnBlockStart(game, "PLAY", button); recorder.OnBlockEnd(game);
        button.CardId = "TB_BaconShop_8p_Reroll_Button"; recorder.OnBlockStart(game, "PLAY", button); recorder.OnBlockEnd(game);
        Assert.Equal(new[] { "buy", "play", "sell", "refresh" }, frames.Where(f => f.Record.Kind == "action").Select(f => Data(f).GetProperty("action").GetString()));
        Assert.All(frames.Where(f => f.Record.Kind == "action"), f => Assert.Equal(JsonValueKind.Null, Data(f).GetProperty("cost").ValueKind));
        Assert.Contains(frames, f => f.Record.Side == "shop" && f.Record.Card == "SHOP_MINION");
        Assert.Equal(4, frames.Count(f => f.Record.Kind == "snapshot"));
        var snapshots = frames.Where(f => f.Record.Kind == "snapshot").ToArray();
        Assert.Equal(5, Data(snapshots[0]).GetProperty("cards")[0].GetProperty("tags").GetProperty("ATK").GetInt32());
        Assert.Equal(7, Data(snapshots[^1]).GetProperty("cards")[0].GetProperty("tags").GetProperty("ATK").GetInt32());
        recorder.OnGameEnded(game);
        Assert.Equal("incomplete", Data(frames.Last()).GetProperty("status").GetString());
    }

    [Fact]
    public void Large_visible_snapshot_is_bounded_to_a_line_and_marks_omitted_cards_as_a_limit_gap()
    {
        var game = Game("GT_BATTLEGROUNDS"); var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add; recorder.SetEnabled(true); recorder.OnGameStarted(game);
        for (var i = 0; i < 100; i++)
        {
            var entity = Entity(game, 1000 + i, new string('X', 80), "HAND", 1, "MINION");
            foreach (var tag in SourceCaptureRecorder.SafeTags) entity.SetTag(tag, int.MinValue.ToString());
            recorder.OnEntityCreated(game, entity);
        }
        recorder.OnBlockStart(game, "TRIGGER", null); recorder.OnBlockEnd(game);
        var snapshot = frames.Last(f => f.Record.Kind == "snapshot");
        Assert.Equal(64, Data(snapshot).GetProperty("cards").GetArrayLength());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(snapshot.Record.ToJson()) + 1, 1, SourceCaptureQueue.MaxLineBytes);
        Assert.Contains(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "limit");
    }

    [Fact]
    public void Draft_offer_packages_and_selection_are_captured_without_a_game_as_partial()
    {
        var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add;
        recorder.SetEnabled(true);
        recorder.CaptureDraftChoice(new ArenaDraftOffer(["DRAFT_A", "DRAFT_B", "DRAFT_C"], true, [[], ["PACKAGE_1", "PACKAGE_2"], []]), 4, 1, new(2026, 11, 9));
        Assert.Equal(new[] { "start", "gap", "draft_choice", "end" }, frames.Select(f => f.Record.Kind));
        Assert.All(frames, f => Assert.Equal("draft", f.CaptureKind));
        var data = Data(frames[2]); Assert.Equal(4, data.GetProperty("pick").GetInt32()); Assert.Equal(1, data.GetProperty("picked").GetInt32());
        Assert.Equal(new[] { "PACKAGE_1", "PACKAGE_2" }, data.GetProperty("options")[1].GetProperty("package").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("incomplete", Data(frames.Last()).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Data(frames.Last()).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Unknown_blocks_non_numeric_tags_and_missing_offer_are_explicit_gaps()
    {
        var (recorder, parser, frames) = Start();
        recorder.OnBlockStart(parser.Game!, "PRIVATE_UNKNOWN_TEXT", null);
        recorder.OnTagChanged(parser.Game!, parser.Game!.Entities[99], "COST", "1", "C:\\Users\\private");
        recorder.Stop("shutdown");
        Assert.Contains(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "unsupported");
        var text = string.Join('\n', frames.Select(f => f.Record.ToJson())); Assert.DoesNotContain("PRIVATE_UNKNOWN_TEXT", text); Assert.DoesNotContain("private", text);
        recorder.CaptureDraftChoice(null, 1, null, new(2026, 11, 9));
        Assert.Contains(frames, f => f.Record.Kind == "gap" && Data(f).GetProperty("reason").GetString() == "invalid");
    }

    [Fact]
    public void Excluded_modes_and_disabled_capture_emit_nothing()
    {
        foreach (var mode in new[] { "GT_VS_FRIEND", "GT_VS_AI", "GT_MERCENARIES", "FUTURE_MODE" })
        {
            var recorder = new SourceCaptureRecorder(); var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add;
            var game = Game(mode); recorder.SetEnabled(true); recorder.OnGameStarted(game); recorder.OnEntityCreated(game, Entity(game, 99, "CARD", "HAND", 1, "MINION"));
            Assert.Empty(frames);
        }
        Assert.False(SourceCaptureClient.Sharing(new AppSettings { PoolEndpoint = "https://capture.test", GameStatsNoticeShown = true }));
        Assert.False(SourceCaptureClient.Sharing(new AppSettings { SourceCaptureNoticeVersion = 1 }));
        Assert.False(SourceCaptureClient.Sharing(new AppSettings { PoolEndpoint = "https://capture.test", SourceCaptureNoticeVersion = 1, GameStatsOff = true }));
        Assert.True(SourceCaptureClient.Sharing(new AppSettings { PoolEndpoint = "https://capture.test", SourceCaptureNoticeVersion = 1 }));
    }

    [Fact]
    public void Checked_in_battlegrounds_fixture_produces_ordered_partial_source_without_post_game_data()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23)); var recorder = new SourceCaptureRecorder();
        var frames = new List<SourceCaptureFrame>(); recorder.Recorded += frames.Add; recorder.SetEnabled(true); parser.AddListener(recorder);
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame", "power.log.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line) parser.Feed(line);
        parser.Flush(); recorder.Stop();
        Assert.Contains(frames, f => f.Record.Kind == "start" && f.Mode == "bg");
        Assert.Contains(frames, f => f.Record.Kind == "choice");
        Assert.Contains(frames, f => f.Record.Kind == "action" && Data(f).GetProperty("action").GetString() == "buy");
        foreach (var group in frames.GroupBy(f => f.CaptureId)) Assert.Equal(Enumerable.Range(0, group.Count()), group.Select(f => f.Record.Seq));
        Assert.All(frames.Where(f => f.Record.Kind == "end"), f => Assert.Equal("incomplete", Data(f).GetProperty("status").GetString()));
    }

    internal static JsonElement Data(SourceCaptureFrame frame) => JsonSerializer.SerializeToElement(frame.Record.Data, SourceCaptureRecord.Json);
    private static (SourceCaptureRecorder Recorder, PowerParser Parser, List<SourceCaptureFrame> Frames) Start()
    {
        var recorder = new SourceCaptureRecorder(); var parser = new PowerParser(new DateOnly(2026, 11, 9)); var frames = new List<SourceCaptureFrame>();
        recorder.Recorded += frames.Add; recorder.SetEnabled(true); parser.AddListener(recorder); ReplayRecorderTests.Feed(parser, ReplayRecorderTests.Setup); parser.Flush(); return (recorder, parser, frames);
    }
    internal static GameState Game(string mode)
    {
        var game = new GameState { LocalPlayerId = 1, GameType = mode, GameEntityId = 1, StartedAt = new(2026, 11, 9) };
        game.GetOrCreate(1); game.Players[1] = new() { PlayerId = 1, EntityId = 2 }; game.Players[2] = new() { PlayerId = 2, EntityId = 3 };
        game.GetOrCreate(2); game.GetOrCreate(3); return game;
    }
    internal static Entity Entity(GameState game, int id, string card, string zone, int controller, string type)
    {
        var entity = game.GetOrCreate(id); entity.CardId = card; entity.SetTag("ZONE", zone); entity.SetTag("CONTROLLER", controller.ToString()); entity.SetTag("CARDTYPE", type); return entity;
    }
}
