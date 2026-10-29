using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Arena;

namespace Hearthpulse.UI;

/// <summary>Loads the Arena numbers (ArenaSmith scores, class results) for the draft helper and the Arena page.</summary>
public sealed partial class AppHost
{
    /// <summary>The Arena numbers of the last successful <see cref="LoadArenaMetaAsync"/>; null until then.</summary>
    public ArenaMeta? ArenaMeta { get; private set; }

    /// <summary>Loaded with the community stats (start-up and every refresh); the API answer is cached for 6 hours.</summary>
    public async Task LoadArenaMetaAsync()
    {
        try
        {
            var meta = await new HearthpulseData(Api).LoadArenaMetaAsync();
            Diag.Info($"Arena meta: {meta.CardCount} card scores, {meta.Classes.Count} classes");
            if (!meta.HasCards && meta.Classes.Count == 0 && ArenaMeta is not null)
            {
                return;
            }

            ArenaMeta = meta;
            Session.SetArenaMeta(meta);
            Main.SetArenaMeta(meta);
        }
        catch (Exception ex)
        {
            Diag.Error("Arena stats unavailable", ex);
            Main.ArenaMetaUnavailable();
        }
    }
}
