using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;

namespace Hearthpulse.Desktop;

/// <summary>--render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman), closed and with its list open.</summary>
internal static class CardPoolRender
{
    public static async Task RenderAsync(AppHost host, AppSettings settings, string folder, Func<string, Task> save)
    {
        var catalog = await CardPoolCatalog.LoadAsync(host.Api, host.Http);
        var meta = await ConstructedMeta.LoadAsync(host.Http, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, fallback: new HearthpulseData(host.Api));
        var engine = new CardPoolEngine(catalog.WithRaces(host.Cards), host.Cards, CardPoolEngine.MetaGames(meta, host.Cards));
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool)
        {
            Console.WriteLine("overlay-card-pool: no card catalog, skipped");
            return;
        }

        var view = new ConstructedView(5, null, false, "HERO_02", "SHAMAN", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            CardPool = pool,
        };
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
        host.Overlay.Update(snapshot, host.Cards, settings, null, null);
        await save(Path.Combine(folder, "overlay-card-pool.png"));
        host.Overlay.ToggleCardPoolList.Execute(null);
        await save(Path.Combine(folder, "overlay-card-pool-list.png"));
        host.Overlay.ToggleCardPoolList.Execute(null);
    }
}
