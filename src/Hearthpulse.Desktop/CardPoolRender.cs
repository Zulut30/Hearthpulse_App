using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Desktop;

/// <summary>
/// --render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman), then the pool browser for
/// Thief's Tools with its filters open, and with one filter on.
/// </summary>
internal static class CardPoolRender
{
    public static async Task RenderAsync(AppHost host, AppSettings settings, string folder, Func<string, Task> save)
    {
        var catalog = await CardPoolCatalog.LoadAsync(host.Api, host.Http);
        var meta = await ConstructedMeta.LoadAsync(new HearthpulseData(host.Api), host.Cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, host.Cards.Locale);
        var engine = new CardPoolEngine(catalog.WithRaces(host.Cards), host.Cards, CardPoolEngine.MetaGames(meta, host.Cards));
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool
            || engine.Build("JAIL_706", new PoolContext("FT_STANDARD", "ROGUE", "WARRIOR"), PoolTrigger.Request) is not { } browse)
        {
            Console.WriteLine("overlay-card-pool: no card catalog, skipped");
            return;
        }

        Show(pool);
        await save(Path.Combine(folder, "overlay-card-pool.png"));

        // A requested pool opens the browser; card renders load after the first layout.
        Show(browse);
        host.Overlay.ToggleCardPoolFilters.Execute(null);
        await SaveBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser.png"));
        host.Overlay.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        await SaveBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser-filtered.png"));
        host.Overlay.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        host.Overlay.ToggleCardPoolFilters.Execute(null);
        host.Overlay.CloseCardPoolBrowser.Execute(null);

        void Show(CardPoolView view)
        {
            var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
            {
                CardPool = view,
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
        }

        async Task SaveBrowserAsync(string path)
        {
            await save(path);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (CardRender.InFlight > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }

            await save(path);
        }
    }
}
