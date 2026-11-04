using Hearthpulse.Core;
using Hearthpulse.Core.Api;

namespace Hearthpulse.UI;

/// <summary>Loads this week's Tavern Brawl by class for the Brawl page (docs/brawl.md).</summary>
public sealed partial class AppHost
{
    /// <summary>Loaded with the community stats (start-up and every refresh); the API answer is cached like the other sets.</summary>
    public async Task LoadBrawlMetaAsync()
    {
        try
        {
            var meta = await new HearthpulseData(Api).LoadBrawlMetaAsync();
            Diag.Info($"Brawl meta: {meta.Classes.Count} classes, {meta.TotalGames} games");
            Main.SetBrawlMeta(meta);
        }
        catch (Exception ex)
        {
            Diag.Error("Brawl stats unavailable", ex);
            Main.BrawlMetaUnavailable();
        }
    }
}
