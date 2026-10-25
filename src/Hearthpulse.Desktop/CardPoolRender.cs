using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Desktop;

/// <summary>
/// --render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman) beside the Discover options
/// and hovered in hand with a chart column under the pointer, a large pool (Swashburglar) on the board, Scion of
/// Insight's rune pool hovered in hand, the "Pool not computed" panel of Dreadsoul Corrupter at its own spot, then the
/// pool browser for Thief's Tools with its filters open, and with one filter on.
/// </summary>
internal static class CardPoolRender
{
    public static async Task RenderAsync(AppHost host, AppSettings settings, string folder, Func<string, Task> save)
    {
        var catalog = await CardPoolCatalog.LoadAsync(host.Api, host.Http);
        var meta = await ConstructedMeta.LoadAsync(new HearthpulseData(host.Api), host.Cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, host.Cards.Locale);
        var wiki = new WikiPoolSource(new HearthpulseData(host.Api));
        await wiki.LoadAsync("BE_298");
        var engine = new CardPoolEngine(catalog.WithRaces(host.Cards), host.Cards, CardPoolEngine.MetaGames(meta, host.Cards), wiki);
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool
            || engine.Build("JAIL_706", new PoolContext("FT_STANDARD", "ROGUE", "WARRIOR"), PoolTrigger.Request) is not { } browse)
        {
            Console.WriteLine("overlay-card-pool: no card catalog, skipped");
            return;
        }

        // Beside the three Discover options.
        Show(pool, new CardPoolAnchor(PoolAnchorZone.Choice, 0, 3));
        await save(Path.Combine(folder, "overlay-card-pool.png"));

        // Hovered in hand (fifth of six cards): the chart on Attack, the pointer on the 3-Attack column.
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Hover) is { } hovered)
        {
            Show(hovered, new CardPoolAnchor(PoolAnchorZone.Hand, 4, 6));
            host.Overlay.HoverCardPoolChart("Attack", null);
            host.Overlay.HoverCardPoolChart(null, 3);
            await save(Path.Combine(folder, "overlay-card-pool-hover.png"));
            host.Overlay.HoverCardPoolChart(null, null);
        }

        // A large pool (Swashburglar, a card from another class) just played onto the board; Death Knight runes (from
        // HearthstoneJSON) hovered in hand; a named group the catalog cannot answer, played: no card on screen, the panel's
        // own spot.
        foreach (var (cardId, ownClass, trigger, anchor, name) in new (string, string, PoolTrigger, CardPoolAnchor?, string)[]
                 {
                     ("CORE_KAR_069", "ROGUE", PoolTrigger.Played, new CardPoolAnchor(PoolAnchorZone.Board, 1, 4), "overlay-card-pool-large.png"),
                     ("BE_298", "DEATHKNIGHT", PoolTrigger.Hover, new CardPoolAnchor(PoolAnchorZone.Hand, 1, 5), "overlay-card-pool-runes.png"),
                     ("EDR_841", "DEMONHUNTER", PoolTrigger.Played, null, "overlay-card-pool-uncomputed.png"),
                 })
        {
            if (engine.Build(cardId, new PoolContext("FT_STANDARD", ownClass, "WARRIOR"), trigger) is { } extra)
            {
                Show(extra, anchor);
                await save(Path.Combine(folder, name));
                if (extra.Sections.Count > 1)
                {
                    // The browser with the wiki's second pool (Frost Rune) picked.
                    host.Overlay.OpenCardPoolBrowser.Execute(null);
                    host.Overlay.ToggleCardPoolSection.Execute(extra.Sections[1].Label);
                    await SaveBrowserAsync(Path.Combine(folder, "overlay-card-pool-sections.png"));
                    host.Overlay.CloseCardPoolBrowser.Execute(null);
                }
            }
        }

        // A requested pool opens the browser; card renders load after the first layout.
        Show(browse);
        host.Overlay.ToggleCardPoolFilters.Execute(null);
        await SaveBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser.png"));
        host.Overlay.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        await SaveBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser-filtered.png"));
        host.Overlay.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        host.Overlay.ToggleCardPoolFilters.Execute(null);
        host.Overlay.CloseCardPoolBrowser.Execute(null);

        void Show(CardPoolView view, CardPoolAnchor? anchor = null)
        {
            var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
            {
                CardPool = view,
                CardPoolAnchor = anchor,
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
