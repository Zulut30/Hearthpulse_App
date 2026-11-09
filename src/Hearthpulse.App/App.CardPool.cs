using System.IO;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>The card pool panel's render scenes (the catalog itself loads in <see cref="Core.Hosting.AppServices.LoadCardPoolAsync"/>).</summary>
public partial class App
{
    /// <summary>
    /// --render-ui: the card pool panel for a real generator (Blazing Invocation as a Shaman) beside the Discover
    /// options and hovered in hand with a chart column under the pointer, a large pool (Swashburglar) at the left of the hand,
    /// Scion of Insight's rune pool hovered in hand, the "Pool not computed" panel of Dreadsoul Corrupter at its own
    /// spot, then the pool browser for Thief's Tools with its filters open, and with one filter on.
    /// </summary>
    private async Task RenderCardPoolAsync(string folder)
    {
        var catalog = await CardPoolCatalog.LoadAsync(_api, _http);
        var wiki = new WikiPoolSource(new Core.Api.HearthpulseData(_api));
        await wiki.LoadAsync("BE_298");
        var engine = new CardPoolEngine(catalog.WithRaces(_cards), _cards, CardPoolEngine.MetaGames(_constructedLoader.Default, _cards), wiki);
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Choice) is not { } pool
            || engine.Build("JAIL_706", new PoolContext("FT_STANDARD", "ROGUE", "WARRIOR"), PoolTrigger.Request) is not { } browse)
        {
            Console.WriteLine("card pool: no catalog, scenes skipped");
            return;
        }

        // Beside the three Discover options; then with the stats pool's picks of those options (sample totals).
        Show(pool, new CardPoolAnchor(PoolAnchorZone.Choice, 0, 3));
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool.png"), 1920, 1080, 0);
        Show(pool, new CardPoolAnchor(PoolAnchorZone.Choice, 0, 3), DiscoverPicks.RenderSample(pool, _cards));
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-picks.png"), 1920, 1080, 0);

        // Hovered in hand (fifth of six cards): the popup beside the card's preview.
        if (engine.Build("CORE_GIL_836", new PoolContext("FT_STANDARD", "SHAMAN", "WARRIOR"), PoolTrigger.Hover) is { } hovered)
        {
            Show(hovered, new CardPoolAnchor(PoolAnchorZone.Hand, 4, 6));
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-hover.png"), 1920, 1080, 0);
        }

        // A large pool (Swashburglar, a card from another class) hovered as the leftmost card in hand; Death Knight runes
        // (from HearthstoneJSON) hovered in hand; a named group the catalog cannot answer, asked for from a deck row: no
        // card on screen, the panel's own spot.
        foreach (var (cardId, ownClass, trigger, anchor, name) in new (string, string, PoolTrigger, CardPoolAnchor?, string)[]
                 {
                     ("CORE_KAR_069", "ROGUE", PoolTrigger.Hover, new CardPoolAnchor(PoolAnchorZone.Hand, 0, 4), "overlay-card-pool-large.png"),
                     ("BE_298", "DEATHKNIGHT", PoolTrigger.Hover, new CardPoolAnchor(PoolAnchorZone.Hand, 1, 5), "overlay-card-pool-runes.png"),
                     ("EDR_841", "DEMONHUNTER", PoolTrigger.Request, null, "overlay-card-pool-uncomputed.png"),
                 })
        {
            if (engine.Build(cardId, new PoolContext("FT_STANDARD", ownClass, "WARRIOR"), trigger) is { } extra)
            {
                Show(extra, anchor);
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

        // N'Zoth in hand with two Deathrattle minions summoned: the "this game" list and his four abilities as two blocks;
        // then the hover card of the list's N'Zoth row with the abilities in a grid beside it.
        if (_session.RelatedCardsOf("BE_017") is { Count: > 0 } abilities)
        {
            string Name(string id) => _cards.Get(id)?.Name ?? id;
            var list = new MatchListView("BE_017", Name("BE_017"), Core.Localization.Loc.T("Pool.List.Summoned"), 2,
                [new MatchListRow("BE_017", Name("BE_017"), 10, "LEGENDARY", 1), new MatchListRow("FP1_001", Name("FP1_001"), 1, "COMMON", 1)]);
            var related = new MatchListView("BE_017", Name("BE_017"), Core.Localization.Loc.T("Related.Title"), abilities.Count, abilities);
            ShowLists(list, related, new CardPoolAnchor(PoolAnchorZone.Hand, 2, 5));
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-nzoth.png"), 1920, 1080, 0);
            if (_overlay.PreviewHost.ShowFirst(_overlay, e => Controls.CardPreview.GetCardId(e) == "BE_017"))
            {
                await RenderBrowserAsync(Path.Combine(folder, "overlay-card-preview-related.png"));
                _overlay.PreviewHost.Hide();
            }
        }

        // Arkedas in hand: his pool, then his hammer in the related cards' zone under it; Wrath alone: the related cards
        // are the panel; Arkedas again at overlay scale 0.8 (the popup is drawn at 0.85 at least).
        if (engine.Build("BE_055", new PoolContext("FT_STANDARD", "WARRIOR", "ROGUE"), PoolTrigger.Hover) is { } arkedas)
        {
            ShowLists(RelatedOf("BE_055"), null, new CardPoolAnchor(PoolAnchorZone.Hand, 3, 5), arkedas);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-related.png"), 1920, 1080, 0);
            var scale = _settings.OverlayScale;
            _settings.OverlayScale = 0.8;
            ShowLists(RelatedOf("BE_055"), null, new CardPoolAnchor(PoolAnchorZone.Hand, 2, 5), arkedas);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-scale08.png"), 1920, 1080, 0);
            _settings.OverlayScale = scale;
        }

        ShowLists(RelatedOf("CORE_EX1_154"), null, new CardPoolAnchor(PoolAnchorZone.Hand, 1, 5));
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-card-pool-related-only.png"), 1920, 1080, 0);

        // A requested pool opens the browser; card renders load after the first layout.
        Show(browse);
        _overlayViewModel.ToggleCardPoolFilters.Execute(null);
        await RenderBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser.png"));
        _overlayViewModel.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        await RenderBrowserAsync(Path.Combine(folder, "overlay-card-pool-browser-filtered.png"));
        _overlayViewModel.ToggleCardPoolMechanic.Execute(browse.Filters[0].Tag);
        _overlayViewModel.ToggleCardPoolFilters.Execute(null);
        _overlayViewModel.CloseCardPoolBrowser.Execute(null);

        void Show(CardPoolView view, CardPoolAnchor? anchor = null, DiscoverPicksView? picks = null)
        {
            var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
            {
                CardPool = view,
                CardPoolAnchor = anchor,
                DiscoverPicks = picks,
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
        }

        // A card without a "this game" rule: the view hands its related cards in MatchList.
        MatchListView RelatedOf(string cardId) =>
            new(cardId, _cards.Get(cardId)?.Name ?? cardId, Core.Localization.Loc.T("Related.Title"), _session.RelatedCardsOf(cardId).Count, _session.RelatedCardsOf(cardId));

        void ShowLists(MatchListView list, MatchListView? related, CardPoolAnchor anchor, CardPoolView? pool = null)
        {
            var constructed = new ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
            {
                CardPool = pool,
                MatchList = list,
                RelatedList = related,
                CardPoolAnchor = anchor,
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
