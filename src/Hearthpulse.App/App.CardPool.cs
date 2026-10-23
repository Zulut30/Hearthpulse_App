using System.IO;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>Loads the card catalog the overlay's card pool panel cuts pools from.</summary>
public partial class App
{
    private async Task LoadCardPoolAsync()
    {
        try
        {
            var catalog = await CardPoolCatalog.LoadAsync(_api, _http);
            _session.SetCardPoolCatalog(catalog);
            Diag.Info($"Card pool: {catalog.Count} cards from {catalog.Source}");
        }
        catch (Exception ex)
        {
            Diag.Error("Card pool catalog unavailable", ex);
        }
    }

    /// <summary>--render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman), closed and with its list open.</summary>
    private async Task RenderCardPoolAsync(string folder)
    {
        var catalog = await CardPoolCatalog.LoadAsync(_api, _http);
        var engine = new CardPoolEngine(catalog.WithRaces(_cards), _cards, CardPoolEngine.MetaGames(_constructedMeta, _cards));
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool)
        {
            Console.WriteLine("card pool: no catalog, scene skipped");
            return;
        }

        var view = new ConstructedView(5, null, false, "HERO_02", "SHAMAN", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            CardPool = pool,
        };
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
        _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool.png"), 1920, 1080, 0);
        _overlayViewModel.ToggleCardPoolList.Execute(null);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-list.png"), 1920, 1080, 0);
        _overlayViewModel.ToggleCardPoolList.Execute(null);
    }
}
