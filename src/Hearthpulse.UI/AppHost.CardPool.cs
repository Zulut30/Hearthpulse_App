using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.UI;

/// <summary>Loads the card catalog the overlay's card pool panel cuts pools from.</summary>
public sealed partial class AppHost
{
    private bool _cardPoolRequested;

    /// <summary>Loads the catalog once, and only while "Show card pool" is on (called at start-up and on settings changes).</summary>
    private async Task LoadCardPoolAsync()
    {
        if (!Settings.ShowCardPool || _cardPoolRequested)
        {
            return;
        }

        _cardPoolRequested = true;
        try
        {
            var catalog = await CardPoolCatalog.LoadAsync(Api, Http);
            Session.SetCardPoolCatalog(catalog);
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
}
