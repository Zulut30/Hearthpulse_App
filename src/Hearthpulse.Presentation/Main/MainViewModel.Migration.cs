#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Migration;
#else
using Hearthpulse.UI.Main.Migration;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>Moving over from HDT / Firestone: the card on the home page and in Settings → Data (<c>docs/migration.md</c>).</summary>
public sealed partial class MainViewModel
{
    private MigrationViewModel? _migration;

    /// <summary>Created on first use: the search for the other trackers' data starts then, off the UI thread.</summary>
    public MigrationViewModel Migration => _migration ??= new MigrationViewModel(() => _cards, _history, Settings, AfterMigrationAsync);

    /// <summary>New games are in History: its lists, My Stats and the decks are read again.</summary>
    private async Task AfterMigrationAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        RebuildDecks();
    }
}
