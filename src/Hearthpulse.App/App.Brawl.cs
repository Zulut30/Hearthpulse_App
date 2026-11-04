using Hearthpulse.Core;
using Hearthpulse.Core.Api;

namespace Hearthpulse.App;

/// <summary>Loads this week's Tavern Brawl by class for the Brawl page (docs/brawl.md).</summary>
public partial class App
{
    /// <summary>Loaded with the community stats (start-up and every refresh); the API answer is cached like the other sets.</summary>
    private async Task LoadBrawlMetaAsync()
    {
        try
        {
            var meta = await new HearthpulseData(_api).LoadBrawlMetaAsync();
            Diag.Info($"Brawl meta: {meta.Classes.Count} classes, {meta.TotalGames} games");
            _mainViewModel.SetBrawlMeta(meta);
        }
        catch (Exception ex)
        {
            Diag.Error("Brawl stats unavailable", ex);
            _mainViewModel.BrawlMetaUnavailable();
        }

        // The week's article of hs-manacost.ru (the owner's site): never throws, nothing current hides the card.
        _mainViewModel.SetBrawlArticles(await new ManacostArticles(_http).LoadAsync());
    }
}
