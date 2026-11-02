using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main.Match;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// The OBS capture windows and streamer mode (docs/streaming.md, TASK-2026-11-02-streamers): what each window shows when,
/// that it works with the overlay off, where it opens, the settings, and exactly what streamer mode hides.
/// </summary>
public sealed class StreamWindowTests
{
    private static LiveSnapshot Game(string type) => LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = type };

    private static LiveSnapshot Constructed(ConstructedView view) => Game("GT_RANKED") with { Constructed = view };

    private static LiveSnapshot Battlegrounds(BattlegroundsView view) => Game("GT_BATTLEGROUNDS") with { Battlegrounds = view };

    private static OverlayViewModel Updated(LiveSnapshot snapshot, AppSettings? settings = null)
    {
        var model = new OverlayViewModel();
        model.Update(snapshot, CardDb.Empty, settings ?? new AppSettings(), null, null);
        return model;
    }

    [Fact]
    public void Windows_and_streamer_mode_are_off_by_default_with_the_theme_background()
    {
        var settings = new AppSettings();
        Assert.False(settings.StreamerMode);
        Assert.False(settings.StreamDeckWindow || settings.StreamLobbyWindow || settings.StreamOddsWindow);
        Assert.False(settings.StreamWindowsOnTop);
        Assert.Equal(StreamBackgrounds.Theme, settings.StreamWindowBackground);
        Assert.Empty(settings.StreamWindows);
        Assert.False(StreamWindowSpec.AnyOn(settings));
    }

    [Fact]
    public void Stream_settings_round_trip_and_bad_values_are_repaired()
    {
        var settings = AppSettings.FromJson("""
            {
              "StreamerMode": true, "StreamOddsWindow": true, "StreamWindowsOnTop": true, "StreamWindowBackground": "Blue",
              "StreamWindows": {
                "deck": null,
                "odds": { "X": 10, "Y": 20, "Width": -5, "Height": 100 },
                "lobby": { "X": 1e9, "Y": 5, "Width": 300, "Height": 1e9 }
              }
            }
            """);
        Assert.True(settings.StreamerMode);
        Assert.True(settings.StreamOddsWindow);
        Assert.True(settings.StreamWindowsOnTop);
        // An unknown background falls back to the theme's; places that are no place go; far ones are pulled in.
        Assert.Equal(StreamBackgrounds.Theme, settings.StreamWindowBackground);
        Assert.Equal(new[] { "lobby" }, settings.StreamWindows.Keys);
        Assert.Equal(StreamWindowPlacement.MaxCoordinate, settings.StreamWindows["lobby"].X);
        Assert.Equal(StreamWindowPlacement.MaxSize, settings.StreamWindows["lobby"].Height);

        Assert.Empty(AppSettings.FromJson("""{ "StreamWindows": null }""").StreamWindows);

        settings.StreamWindowBackground = StreamBackgrounds.Magenta;
        var again = AppSettings.FromJson(settings.ToJson());
        Assert.Equal(StreamBackgrounds.Magenta, again.StreamWindowBackground);
        Assert.True(again.StreamBackgroundMagenta);
        Assert.False(again.StreamBackgroundGreen);
        Assert.Equal(300, again.StreamWindows["lobby"].Width);
        // The radio helpers are not saved.
        Assert.DoesNotContain("StreamBackgroundGreen", settings.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Background_radios_pick_one_and_ignore_unchecking()
    {
        var settings = new AppSettings { StreamBackgroundGreen = true };
        Assert.Equal(StreamBackgrounds.Green, settings.StreamWindowBackground);
        settings.StreamBackgroundGreen = false;
        Assert.Equal(StreamBackgrounds.Green, settings.StreamWindowBackground);
        Assert.Null(StreamBackdrop.KeyColor(StreamBackgrounds.Theme));
        Assert.Equal(((byte)0, (byte)255, (byte)0), StreamBackdrop.KeyColor(StreamBackgrounds.Green));
        Assert.Equal(((byte)255, (byte)0, (byte)255), StreamBackdrop.KeyColor(StreamBackgrounds.Magenta));
    }

    [Fact]
    public void The_deck_window_shows_the_deck_in_a_game_other_than_battlegrounds()
    {
        var known = StreamScene.Constructed(CardDb.Empty);
        Assert.Equal(StreamWindowState.Idle, Updated(LiveSnapshot.Empty).DeckWindowState);
        Assert.Equal(StreamWindowState.Idle, Updated(Constructed(known) with { InGame = false }).DeckWindowState);
        Assert.Equal(StreamWindowState.Waiting, Updated(Constructed(known with { DeckKnown = false })).DeckWindowState);
        Assert.Equal(StreamWindowState.Shown, Updated(Constructed(known)).DeckWindowState);
        Assert.Equal(StreamWindowState.Shown, Updated(Constructed(known) with { GameType = "GT_ARENA" }).DeckWindowState);
        Assert.Equal(StreamWindowState.Idle, Updated(Battlegrounds(StreamScene.Lobby(CardDb.Empty))).DeckWindowState);
        // The Arena draft is no game: the tile.
        Assert.Equal(StreamWindowState.Idle, Updated(LiveSnapshot.Empty with { Scene = "DRAFT", HearthstoneRunning = true }).DeckWindowState);
    }

    [Fact]
    public void The_lobby_and_odds_windows_show_battlegrounds_only_and_keep_the_background_between()
    {
        var lobby = StreamScene.Lobby(CardDb.Empty);
        var recruiting = Updated(Battlegrounds(lobby));
        Assert.Equal(StreamWindowState.Shown, recruiting.LobbyWindowState);
        Assert.Equal(StreamWindowState.Waiting, recruiting.OddsWindowState);

        var heroPick = Updated(Battlegrounds(lobby with { IsHeroSelection = true, HeroCardId = null, Opponents = [] }));
        Assert.Equal(StreamWindowState.Waiting, heroPick.LobbyWindowState);
        Assert.Equal(StreamWindowState.Waiting, heroPick.OddsWindowState);

        var fight = Updated(Battlegrounds(CombatOddsScene.Synthetic()));
        Assert.Equal(StreamWindowState.Shown, fight.OddsWindowState);

        var duos = Updated(Battlegrounds(lobby with { IsDuos = true }) with { GameType = "GT_BATTLEGROUNDS_DUO" });
        Assert.Equal(StreamWindowState.Shown, duos.LobbyWindowState);

        var ranked = Updated(Constructed(StreamScene.Constructed(CardDb.Empty)));
        Assert.Equal(StreamWindowState.Idle, ranked.LobbyWindowState);
        Assert.Equal(StreamWindowState.Idle, ranked.OddsWindowState);
        Assert.Equal(StreamWindowState.Idle, Updated(LiveSnapshot.Empty).OddsWindowState);
        Assert.Equal(nameof(OverlayViewModel.OddsWindowState), OverlayViewModel.StreamStateProperty("odds"));
        Assert.Equal(StreamWindowState.Shown, recruiting.StreamState("lobby"));
    }

    [Fact]
    public void A_window_works_with_the_overlay_and_its_panel_off()
    {
        var view = StreamScene.Constructed(CardDb.Empty);
        var settings = new AppSettings { OverlayEnabled = false, ShowDeckTracker = false };
        var off = Updated(Constructed(view), settings);
        // Neither the overlay nor a window: the game is not read.
        Assert.Empty(off.Deck);

        settings.StreamDeckWindow = true;
        var window = Updated(Constructed(view), settings);
        Assert.Equal(StreamWindowState.Shown, window.DeckWindowState);
        Assert.Equal(view.Deck.Count, window.Deck.Count);
        Assert.Equal("Paladin", window.DeckName);
        // The overlay's own panel still follows its switch.
        Assert.False(window.ShowConstructed);

        var bg = new AppSettings { OverlayEnabled = false, ShowLobbyPanel = false, StreamLobbyWindow = true };
        var lobby = Updated(Battlegrounds(StreamScene.Lobby(CardDb.Empty)), bg);
        Assert.False(lobby.ShowLobby);
        Assert.Equal(8, lobby.LobbyRows.Count);
    }

    [Fact]
    public void Streamer_mode_puts_the_class_for_the_opponents_name_and_drops_the_rank()
    {
        var view = StreamScene.Constructed(CardDb.Empty);
        var priest = OverlayViewModel.ClassName("PRIEST");
        var rank = view.OpponentRank!.Display;

        var normal = Updated(Constructed(view));
        Assert.Equal(StreamScene.OpponentBattleTag, normal.OpponentTitle);
        Assert.Contains(priest, normal.OpponentSubtitle, StringComparison.Ordinal);
        Assert.Contains(rank, normal.OpponentSubtitle, StringComparison.Ordinal);

        var settings = new AppSettings { StreamerMode = true };
        var streamer = Updated(Constructed(view), settings);
        Assert.True(streamer.StreamerMode);
        Assert.Equal(priest, streamer.OpponentTitle);
        // The second line stays (the header keeps its height) but says nothing about the opponent.
        Assert.Equal(Loc.T("Overlay.Common.Opponent"), streamer.OpponentSubtitle);
        Assert.DoesNotContain("#2947", streamer.OpponentTitle + streamer.OpponentSubtitle, StringComparison.Ordinal);
        Assert.DoesNotContain(rank, streamer.OpponentTitle + streamer.OpponentSubtitle, StringComparison.Ordinal);

        // Before the class is known: "Opponent".
        var unknown = Updated(Constructed(view with { OpponentClass = string.Empty }), settings);
        Assert.Equal(Loc.T("Overlay.Common.Opponent"), unknown.OpponentTitle);

        // Switched back on the next update: the name returns.
        settings.StreamerMode = false;
        streamer.Update(Constructed(view), CardDb.Empty, settings, null, null);
        Assert.Equal(StreamScene.OpponentBattleTag, streamer.OpponentTitle);
    }

    [Fact]
    public void Streamer_mode_keeps_the_players_own_data_on_the_overlay()
    {
        var view = StreamScene.Constructed(CardDb.Empty);
        var normal = Updated(Constructed(view));
        var streamer = Updated(Constructed(view), new AppSettings { StreamerMode = true });
        Assert.Equal(normal.DeckName, streamer.DeckName);
        Assert.Equal(normal.DeckCounts, streamer.DeckCounts);
        Assert.Equal(normal.Deck.Select(d => d.Name), streamer.Deck.Select(d => d.Name));

        var lobby = StreamScene.Lobby(CardDb.Empty);
        var bgNormal = Updated(Battlegrounds(lobby));
        var bgStreamer = Updated(Battlegrounds(lobby), new AppSettings { StreamerMode = true });
        Assert.Equal(bgNormal.LobbyRows.Select(r => r.Name), bgStreamer.LobbyRows.Select(r => r.Name));
    }

    [Fact]
    public void Streamer_mode_hides_both_battletags_and_the_opponents_rank_on_the_match_header()
    {
        var header = new MatchHeader("Paladin vs Priest", "Ranked", "Victory", "Win",
            new MatchPlayerView("Me", "HERO_04", "Uther", "Paladin", "Diamond 5"),
            new MatchPlayerView("Rival", "HERO_09", "Anduin", "Priest", "Diamond 4"));

        Assert.Same(header, MatchViewModel.Mask(header, battlegrounds: false, streamer: false));

        var masked = MatchViewModel.Mask(header, battlegrounds: false, streamer: true);
        Assert.Equal(Loc.T("Match.You"), masked.Local.Name);
        Assert.Equal("Diamond 5", masked.Local.Rank);
        Assert.Equal("Priest", masked.Opponent.Name);
        Assert.Equal(string.Empty, masked.Opponent.Rank);
        Assert.Equal(header.Title, masked.Title);

        var noClass = MatchViewModel.Mask(header with { Opponent = header.Opponent with { Class = string.Empty } }, battlegrounds: false, streamer: true);
        Assert.Equal(Loc.T("Match.Opponent"), noClass.Opponent.Name);

        var lobby = header with { Opponent = header.Opponent with { Name = Loc.T("Match.Lobby") }, Local = header.Local with { Rank = "6 500" } };
        var bg = MatchViewModel.Mask(lobby, battlegrounds: true, streamer: true);
        Assert.Equal(Loc.T("Match.Lobby"), bg.Opponent.Name);
        Assert.Equal(Loc.T("Match.You"), bg.Local.Name);
        Assert.Equal("6 500", bg.Local.Rank);
    }

    [Fact]
    public void A_window_opens_at_its_place_or_its_default_spot()
    {
        var primary = new StreamRect(0, 0, 1920, 1040);
        StreamRect[] areas = [primary, new StreamRect(1920, 0, 1920, 1040)];

        // Never moved: the default spots side by side at the top left of the primary work area.
        Assert.Equal(new StreamRect(24, 24, 268, 720), StreamWindowLayout.Place(StreamWindowSpec.Deck, null, areas, primary));
        Assert.Equal(new StreamRect(308, 24, 246, 306), StreamWindowLayout.Place(StreamWindowSpec.Lobby, null, areas, primary));
        Assert.Equal(new StreamRect(570, 24, 266, 140), StreamWindowLayout.Place(StreamWindowSpec.Odds, null, areas, primary));
        Assert.Equal(new StreamRect(124, 74, 268, 720), StreamWindowLayout.Place(StreamWindowSpec.Deck, null, areas, new StreamRect(100, 50, 1800, 1000)));

        // Saved on the second monitor: kept.
        var saved = new StreamWindowPlacement { X = 2500, Y = 100, Width = 400, Height = 900 };
        Assert.Equal(new StreamRect(2500, 100, 400, 900), StreamWindowLayout.Place(StreamWindowSpec.Deck, saved, areas, primary));
        // That monitor is gone: the default spot, the saved size.
        Assert.Equal(new StreamRect(24, 24, 400, 900), StreamWindowLayout.Place(StreamWindowSpec.Deck, saved, [primary], primary));
        // Only a sliver (30 wide) still on a screen is not enough.
        var sliver = new StreamWindowPlacement { X = 1890, Y = 100, Width = 300, Height = 300 };
        Assert.Equal(new StreamRect(570, 24, 300, 300), StreamWindowLayout.Place(StreamWindowSpec.Odds, sliver, [primary], primary));
        // Never below the smallest size.
        var tiny = new StreamWindowPlacement { X = 100, Y = 100, Width = 20, Height = 20 };
        Assert.Equal(new StreamRect(100, 100, 156, 120), StreamWindowLayout.Place(StreamWindowSpec.Lobby, tiny, areas, primary));
    }

    [Fact]
    public void A_window_keeps_its_place_by_its_id_and_original_size_keeps_the_spot()
    {
        var settings = new AppSettings();
        StreamWindowLayout.Save(settings, StreamWindowSpec.Odds, new StreamRect(40, 50, 300, 160));
        var odds = settings.StreamWindows["odds"];
        Assert.Equal((40.0, 50.0, 300.0, 160.0), (odds.X, odds.Y, odds.Width, odds.Height));
        StreamWindowLayout.Save(settings, StreamWindowSpec.Deck, new StreamRect(double.NaN, 0, 10, 10));
        Assert.False(settings.StreamWindows.ContainsKey("deck"));
        Assert.Equal(new StreamRect(40, 50, 266, 140), StreamWindowLayout.OriginalSize(StreamWindowSpec.Odds, new StreamRect(40, 50, 300, 160)));

        // The ids are the overlay panels' (never renamed: saved places would be lost).
        Assert.Equal(new[] { "deck", "lobby", "odds" }, StreamWindowSpec.All.Select(s => s.Id));
        foreach (var spec in StreamWindowSpec.All)
        {
            Assert.False(spec.IsOn(settings));
            spec.SetOn(settings, true);
            Assert.True(spec.IsOn(settings));
            Assert.StartsWith("Hearthpulse — ", spec.Title, StringComparison.Ordinal);
            Assert.Equal(Loc.F("Stream.Window.Title", Loc.T(spec.NameKey)), spec.Title);
        }

        Assert.True(settings.StreamDeckWindow && settings.StreamLobbyWindow && settings.StreamOddsWindow);
    }
}
