using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Arena;

namespace Hearthpulse.App;

/// <summary>Loads the Arena numbers (ArenaSmith scores, class results) for the draft helper and the Arena page.</summary>
public partial class App
{
    private ArenaMeta? _arenaMeta;

    /// <summary>Loaded with the community stats (start-up and every refresh); the API answer is cached for 6 hours.</summary>
    private async Task LoadArenaMetaAsync()
    {
        try
        {
            var meta = await new HearthpulseData(_api).LoadArenaMetaAsync();
            Diag.Info($"Arena meta: {meta.CardCount} card scores, {meta.Classes.Count} classes");
            if (!meta.HasCards && meta.Classes.Count == 0 && _arenaMeta is not null)
            {
                return;
            }

            _arenaMeta = meta;
            _session.SetArenaMeta(meta);
            _mainViewModel.SetArenaMeta(meta);
        }
        catch (Exception ex)
        {
            Diag.Error("Arena stats unavailable", ex);
            _mainViewModel.ArenaMetaUnavailable();
        }
    }
}
