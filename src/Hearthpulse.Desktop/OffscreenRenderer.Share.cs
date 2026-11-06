using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Share;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>, group <c>share</c>: the share pictures as they are saved (TASK-2026-11-07-growth-app).</summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderShareAsync(AppHost host, string folder)
    {
        await RenderSharePictureAsync(host.Main.SessionRecapShare(), folder, "share-recap.png");
        var deck = host.History.Recent(400).FirstOrDefault(g => !string.IsNullOrEmpty(g.DeckCode) && g.Mode == "Ranked");
        if (host.Main.DeckShare(deck?.DeckCode, deck?.DeckName, deck?.PlayerClass) is { } share)
        {
            await RenderSharePictureAsync(share, folder, "share-deck-list.png");
            share.Cover = true;
            await RenderSharePictureAsync(share, folder, "share-deck-cover.png");
        }

        if (host.Main.YearShare() is { } year)
        {
            await RenderSharePictureAsync(year, folder, "share-year.png");
        }

        // The OBS window "Session" over the latest session of the history.
        var games = host.History.Recent(400);
        var since = SessionRecap.LastSession(games, TimeSpan.FromHours(host.Settings.LadderSessionBreakHours))?.Since ?? DateTime.Now;
        host.Overlay.Update(LiveSnapshot.Empty with { StreamSession = StreamSession.Build(games, since) }, host.Cards, host.Settings, null, null);
        await SaveStreamWindowAsync(host, host.Settings, StreamWindowSpec.Session, folder, "stream-session.png");
        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, host.Settings, null, null);
    }

    /// <summary>The picture exactly as "Save" writes it (twice its size), after its card art arrived.</summary>
    private static async Task RenderSharePictureAsync(ShareViewModel model, string folder, string name)
    {
        var window = new ShareWindow(model);
        window.Show();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            do
            {
                await Task.Delay(250);
                Dispatcher.UIThread.RunJobs();
            }
            while (CardArt.InFlight > 0 && DateTime.UtcNow < deadline);

            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var path = Path.Combine(folder, name);
            await File.WriteAllBytesAsync(path, window.Encode());
            Console.WriteLine("wrote " + path);
        }
        finally
        {
            window.Close();
        }
    }
}
