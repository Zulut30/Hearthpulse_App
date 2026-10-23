using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.UI;

/// <summary>Loads the card catalog the overlay's card pool panel cuts pools from.</summary>
public sealed partial class AppHost
{
    private async Task LoadCardPoolAsync()
    {
        try
        {
            var catalog = await CardPoolCatalog.LoadAsync(Api, Http);
            Session.SetCardPoolCatalog(catalog);
            Diag.Info($"Card pool: {catalog.Count} cards from {catalog.Source}");
        }
        catch (Exception ex)
        {
            Diag.Error("Card pool catalog unavailable", ex);
        }
    }
}
