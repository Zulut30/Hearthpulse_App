using System.IO;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>
/// <c>--render-ui</c>: the OBS capture windows and streamer mode (docs/streaming.md) on made-up games (no kept log needed):
/// <c>stream-deck.png</c>, <c>stream-deck-green.png</c>, <c>stream-lobby.png</c>, <c>stream-odds.png</c>,
/// <c>stream-odds-magenta.png</c>, <c>stream-idle.png</c> (the tile in the smallest lobby window) and
/// <c>overlay-constructed-streamer.png</c> (the opponent's header without the BattleTag). The Stream tab of Settings is in
/// the settings tab loop (<c>main-settings-stream.png</c>).
/// </summary>
public partial class App
{
    private async Task RenderStreamAsync(string folder)
    {
        var (streamer, background) = (_settings.StreamerMode, _settings.StreamWindowBackground);
        try
        {
            var constructed = Snapshot("GT_RANKED") with { Constructed = StreamScene.Constructed(_cards) };
            _overlayViewModel.Update(constructed, _cards, _settings, null, null);
            await RenderStreamWindowAsync(StreamWindowSpec.Deck, folder, "stream-deck.png");
            _settings.StreamWindowBackground = StreamBackgrounds.Green;
            await RenderStreamWindowAsync(StreamWindowSpec.Deck, folder, "stream-deck-green.png");
            _settings.StreamWindowBackground = StreamBackgrounds.Theme;

            _settings.StreamerMode = true;
            _overlayViewModel.Update(constructed, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-constructed-streamer.png"), 1920, 1080, 0);
            _settings.StreamerMode = streamer;

            _overlayViewModel.Update(Snapshot("GT_BATTLEGROUNDS") with { Battlegrounds = StreamScene.Lobby(_cards) }, _cards, _settings, null, null);
            await RenderStreamWindowAsync(StreamWindowSpec.Lobby, folder, "stream-lobby.png");

            _overlayViewModel.Update(Snapshot("GT_BATTLEGROUNDS") with { Battlegrounds = CombatOddsScene.Synthetic() }, _cards, _settings, null, null);
            await RenderStreamWindowAsync(StreamWindowSpec.Odds, folder, "stream-odds.png");
            _settings.StreamWindowBackground = StreamBackgrounds.Magenta;
            await RenderStreamWindowAsync(StreamWindowSpec.Odds, folder, "stream-odds-magenta.png");
            _settings.StreamWindowBackground = StreamBackgrounds.Theme;

            // No game: the quiet tile in the smallest lobby window.
            _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
            await RenderStreamWindowAsync(StreamWindowSpec.Lobby, folder, "stream-idle.png", StreamWindowSpec.Lobby.MinWidth, StreamWindowSpec.Lobby.MinHeight);
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the OBS windows failed", ex);
        }
        finally
        {
            (_settings.StreamerMode, _settings.StreamWindowBackground) = (streamer, background);
            _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
        }

        static LiveSnapshot Snapshot(string gameType) => LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = gameType };
    }

    /// <summary>One OBS window at its default size (or the one given), off screen, as OBS would capture it.</summary>
    private async Task RenderStreamWindowAsync(StreamWindowSpec spec, string folder, string name, double? width = null, double? height = null)
    {
        var window = _stream!.Build(spec);
        try
        {
            await window.RenderToFileAsync(Path.Combine(folder, name), (int)(width ?? spec.Width), (int)(height ?? spec.Height));
        }
        finally
        {
            window.CloseForReal();
        }
    }
}
