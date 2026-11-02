using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// <c>--render-ui</c>: the OBS capture windows and streamer mode (docs/streaming.md) on made-up games (no kept log needed):
/// <c>stream-deck.png</c>, <c>stream-deck-green.png</c>, <c>stream-lobby.png</c>, <c>stream-odds.png</c>,
/// <c>stream-odds-magenta.png</c>, <c>stream-idle.png</c> (the tile in the smallest lobby window),
/// <c>overlay-constructed-streamer.png</c> (the opponent's header without the BattleTag) and
/// <c>main-settings-stream-compact.png</c>. The Stream tab at full width is in the settings tab loop.
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderStreamAsync(Window main, MainView view, AppHost host, AppSettings settings, string folder)
    {
        var (streamer, background) = (settings.StreamerMode, settings.StreamWindowBackground);
        var (width, height) = (main.Width, main.Height);
        try
        {
            // Compact: the tabs wrap, the background row puts its label over the segments.
            main.Width = 420;
            main.Height = 860;
            view.ShowPage("Settings");
            view.ShowSettingsTab("Stream");
            Dispatcher.UIThread.RunJobs();
            await Save(main, Path.Combine(folder, "main-settings-stream-compact.png"));

            var constructed = Snapshot("GT_RANKED") with { Constructed = StreamScene.Constructed(host.Cards) };
            host.Overlay.Update(constructed, host.Cards, settings, null, null);
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Deck, folder, "stream-deck.png");
            settings.StreamWindowBackground = StreamBackgrounds.Green;
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Deck, folder, "stream-deck-green.png");
            settings.StreamWindowBackground = StreamBackgrounds.Theme;

            settings.StreamerMode = true;
            host.Overlay.Update(constructed, host.Cards, settings, null, null);
            var overlayView = new OverlayView { DataContext = host.Overlay, Settings = settings };
            var overlay = new Window { Width = 1920, Height = 1080, Content = overlayView, Background = ThemeBrushes.Get("Bg") };
            overlay.Show();
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-constructed-streamer.png"));
            overlay.Close();
            settings.StreamerMode = streamer;

            host.Overlay.Update(Snapshot("GT_BATTLEGROUNDS") with { Battlegrounds = StreamScene.Lobby(host.Cards) }, host.Cards, settings, null, null);
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Lobby, folder, "stream-lobby.png");

            host.Overlay.Update(Snapshot("GT_BATTLEGROUNDS") with { Battlegrounds = CombatOddsScene.Synthetic() }, host.Cards, settings, null, null);
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Odds, folder, "stream-odds.png");
            settings.StreamWindowBackground = StreamBackgrounds.Magenta;
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Odds, folder, "stream-odds-magenta.png");
            settings.StreamWindowBackground = StreamBackgrounds.Theme;

            // No game: the quiet tile in the smallest lobby window.
            host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
            await SaveStreamWindowAsync(host, settings, StreamWindowSpec.Lobby, folder, "stream-idle.png", StreamWindowSpec.Lobby.MinWidth, StreamWindowSpec.Lobby.MinHeight);
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the OBS windows failed", ex);
        }
        finally
        {
            (settings.StreamerMode, settings.StreamWindowBackground) = (streamer, background);
            host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
            (main.Width, main.Height) = (width, height);
            view.ShowSettingsTab("General");
        }

        static LiveSnapshot Snapshot(string gameType) => LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = gameType };
    }

    /// <summary>One OBS window's content at its default size (or the one given), as OBS would capture it.</summary>
    private static async Task SaveStreamWindowAsync(AppHost host, AppSettings settings, StreamWindowSpec spec, string folder, string name, double? width = null, double? height = null)
    {
        var content = new StreamView { Spec = spec, DataContext = host.Overlay };
        content.SetBackground(settings.StreamWindowBackground);
        var window = new Window { Width = width ?? spec.Width, Height = height ?? spec.Height, Content = content };
        window.Show();
        try
        {
            await Save(window, Path.Combine(folder, name));
        }
        finally
        {
            window.Close();
        }
    }
}
