using System.IO;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>Loads the card catalog the overlay's card pool panel cuts pools from.</summary>
public partial class App
{
    private bool _cardPoolRequested;

    /// <summary>Loads the catalog once, and only while "Show card pool" is on (called at start-up and on settings changes).</summary>
    private async Task LoadCardPoolAsync()
    {
        if (!_settings.ShowCardPool || _cardPoolRequested)
        {
            return;
        }

        _cardPoolRequested = true;
        try
        {
            var catalog = await CardPoolCatalog.LoadAsync(_api, _http);
            _session.SetCardPoolCatalog(catalog);
            Diag.Info($"Card pool: {catalog.Count} cards from {catalog.Source}");
            // Nothing could be loaded (offline, no cache): try again on the next settings change or start.
            _cardPoolRequested = catalog.Count > 0;
        }
        catch (Exception ex)
        {
            _cardPoolRequested = false;
            Diag.Error("Card pool catalog unavailable", ex);
        }
    }

    /// <summary>
    /// --render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman), Scion of Insight's rune
    /// pool, the "Pool not computed" panel of Dreadsoul Corrupter, then the pool browser for Thief's Tools with its
    /// filters open, and with one filter on.
    /// </summary>
    private async Task RenderCardPoolAsync(string folder)
    {
        var catalog = await CardPoolCatalog.LoadAsync(_api, _http);
        var wiki = new WikiPoolSource(new Core.Api.HearthpulseData(_api));
        await wiki.LoadAsync("BE_298");
        var engine = new CardPoolEngine(catalog.WithRaces(_cards), _cards, CardPoolEngine.MetaGames(_constructedMeta, _cards), wiki);
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool
            || engine.Build("JAIL_706", new PoolContext("FT_STANDARD", "ROGUE", "WARRIOR"), PoolTrigger.Request) is not { } browse)
        {
            Console.WriteLine("card pool: no catalog, scenes skipped");
            return;
        }

        Show(pool);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool.png"), 1920, 1080, 0);

        // Death Knight runes (from HearthstoneJSON) and a named group the catalog cannot answer.
        foreach (var (cardId, ownClass, trigger, name) in new[]
                 {
                     ("BE_298", "DEATHKNIGHT", PoolTrigger.Choice, "overlay-card-pool-runes.png"),
                     ("EDR_841", "DEMONHUNTER", PoolTrigger.Played, "overlay-card-pool-uncomputed.png"),
                 })
        {
            if (engine.Build(cardId, new PoolContext("FT_STANDARD", ownClass, "WARRIOR"), trigger) is { } extra)
            {
                Show(extra);
                await _overlay.RenderToFileAsync(Path.Combine(folder, name), 1920, 1080, 0);
                if (extra.Sections.Count > 1)
                {
                    // The browser with the wiki's second pool (Frost Rune) picked.
                    _overlayViewModel.OpenCardPoolBrowser.Execute(null);
                    _overlayViewModel.ToggleCardPoolSection.Execute(extra.Sections[1].Label);
                    await RenderBrowserAsync(Path.Combine(folder, "overlay-card-pool-sections.png"));
                    _overlayViewModel.CloseCardPoolBrowser.Execute(null);
                }
            }
        }

        // A requested pool opens the browser; card renders load after the first layout.
        Show(browse);
        _overlayViewModel.ToggleCardPoolFilters.Execute(null);
        await RenderBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser.png"));
        _overlayViewModel.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        await RenderBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser-filtered.png"));
        _overlayViewModel.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        _overlayViewModel.ToggleCardPoolFilters.Execute(null);
        _overlayViewModel.CloseCardPoolBrowser.Execute(null);

        void Show(CardPoolView view)
        {
            var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
            {
                CardPool = view,
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
        }

        async Task RenderBrowserAsync(string path)
        {
            await _overlay.RenderToFileAsync(path, 1920, 1080, 0);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (CardRender.InFlight > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            await _overlay.RenderToFileAsync(path, 1920, 1080, 0);
        }
    }
}
