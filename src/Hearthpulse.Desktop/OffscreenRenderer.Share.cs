using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Hearthpulse.Core.Cards;
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

        if (await host.Main.YearShareAsync() is { } year)
        {
            await RenderSharePictureAsync(year, folder, "share-year.png");
        }

        // YouTube timestamps of the latest session.
        var stamps = new TimestampsWindow(host.Main.Timestamps());
        stamps.Show();
        try
        {
            await Save(stamps, Path.Combine(folder, "share-timestamps.png"));
        }
        finally
        {
            stamps.Close();
        }

        // The OBS window "Session" over the latest session of the history.
        var games = host.History.Recent(400);
        var since = SessionRecap.LastSession(games, TimeSpan.FromHours(host.Settings.LadderSessionBreakHours))?.Since ?? DateTime.Now;
        host.Overlay.Update(LiveSnapshot.Empty with { StreamSession = StreamSession.Build(games, since) }, host.Cards, host.Settings, null, null);
        await SaveStreamWindowAsync(host, host.Settings, StreamWindowSpec.Session, folder, "stream-session.png");
        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, host.Settings, null, null);
    }

    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=sharecards</c>: every share picture on fixed decks (TASK-2026-11-09-deck-cover): the
    /// session recap, the year, and a Warrior deck of 21, 30, 15, 12 and 8 rows in both layouts, long card names included.
    /// Each layout gets a fresh model: a model is never changed after its window closed (TASK-2026-11-09-deck-cover, M2).
    /// </summary>
    private static async Task RenderShareCardsAsync(AppHost host, string folder)
    {
        await RenderSharePictureAsync(host.Main.SessionRecapShare(), folder, "share-recap.png");
        if (await host.Main.YearShareAsync() is { } year)
        {
            await RenderSharePictureAsync(year, folder, "share-year.png");
        }

        foreach (var (rows, deck) in ShareSampleDecks.All(host.Cards))
        {
            if (DeckString.Encode(deck, "WARRIOR", "Standard", host.Cards) is { } code && host.Main.DeckShare(code, string.Empty, "WARRIOR") is { } share)
            {
                await RenderSharePictureAsync(share, folder, $"share-deck-list-{rows}.png");
                var cover = host.Main.DeckShare(code, string.Empty, "WARRIOR")!;
                cover.Cover = true;
                await RenderSharePictureAsync(cover, folder, $"share-deck-cover-{rows}.png");
            }
        }
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
