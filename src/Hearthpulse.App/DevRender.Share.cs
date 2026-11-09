using System.IO;
using System.Windows;
using Hearthpulse.App.Main.Share;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>, group <c>share</c>: the share pictures as they are saved (TASK-2026-11-07-growth-app).</summary>
public partial class App
{
    private async Task RenderShareAsync(string folder)
    {
        try
        {
            await RenderSharePictureAsync(_mainViewModel.SessionRecapShare(), folder, "share-recap.png");
            var deck = _history.Recent(400).FirstOrDefault(g => !string.IsNullOrEmpty(g.DeckCode) && g.Mode == "Ranked");
            if (_mainViewModel.DeckShare(deck?.DeckCode, deck?.DeckName, deck?.PlayerClass) is { } share)
            {
                await RenderSharePictureAsync(share, folder, "share-deck-list.png");
                share.Cover = true;
                await RenderSharePictureAsync(share, folder, "share-deck-cover.png");
            }

            if (await _mainViewModel.YearShareAsync() is { } year)
            {
                await RenderSharePictureAsync(year, folder, "share-year.png");
            }

            // YouTube timestamps of the latest session.
            var stamps = new TimestampsWindow(_mainViewModel.Timestamps()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
            stamps.Show();
            await Task.Delay(500);
            stamps.UpdateLayout();
            Save(stamps, Path.Combine(folder, "share-timestamps.png"));
            stamps.Close();

            // The OBS window "Session" over the latest session of the history.
            var games = _history.Recent(400);
            var since = SessionRecap.LastSession(games, TimeSpan.FromHours(_settings.LadderSessionBreakHours))?.Since ?? DateTime.Now;
            _overlayViewModel.Update(LiveSnapshot.Empty with { StreamSession = StreamSession.Build(games, since) }, _cards, _settings, null, null);
            await RenderStreamWindowAsync(StreamWindowSpec.Session, folder, "stream-session.png");
            _overlayViewModel.Update(LiveSnapshot.Empty, _cards, _settings, null, null);
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the share pictures failed", ex);
        }
    }

    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=sharecards</c>: every share picture on fixed decks (TASK-2026-11-09-deck-cover): the
    /// session recap, the year, and a Warrior deck of 21, 30, 15, 12 and 8 rows in both layouts, long card names included.
    /// Each layout gets a fresh model: a model is never changed after its window closed (TASK-2026-11-09-deck-cover, M2).
    /// </summary>
    private async Task RenderShareCardsAsync(string folder)
    {
        try
        {
            await RenderSharePictureAsync(_mainViewModel.SessionRecapShare(), folder, "share-recap.png");
            if (await _mainViewModel.YearShareAsync() is { } year)
            {
                await RenderSharePictureAsync(year, folder, "share-year.png");
            }

            foreach (var (rows, deck) in ShareSampleDecks.All(_cards))
            {
                if (DeckString.Encode(deck, "WARRIOR", "Standard", _cards) is { } code && _mainViewModel.DeckShare(code, string.Empty, "WARRIOR") is { } share)
                {
                    await RenderSharePictureAsync(share, folder, $"share-deck-list-{rows}.png");
                    var cover = _mainViewModel.DeckShare(code, string.Empty, "WARRIOR")!;
                    cover.Cover = true;
                    await RenderSharePictureAsync(cover, folder, $"share-deck-cover-{rows}.png");
                }
            }
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the share cards failed", ex);
        }
    }

    /// <summary>The picture exactly as "Save" writes it (twice its size), after its card art arrived.</summary>
    private static async Task RenderSharePictureAsync(ShareViewModel model, string folder, string name)
    {
        var window = new ShareWindow(model) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        try
        {
            await Task.Delay(2500);
            window.UpdateLayout();
            await File.WriteAllBytesAsync(Path.Combine(folder, name), window.Encode());
        }
        finally
        {
            window.Close();
        }
    }
}
