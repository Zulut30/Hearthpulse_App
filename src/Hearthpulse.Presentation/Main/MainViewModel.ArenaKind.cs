using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The Arena page's two tabs (owner, 2026-11-07): "Arena" and "Underground Arena". Stats, runs, the deck score and the
/// community class win rates follow the tab; numbers of one Arena never stand in for the other's.
/// </summary>
public sealed partial class MainViewModel
{
    private ArenaKind _arenaKind = ArenaKind.Arena;

    /// <summary>The Arena the page shows.</summary>
    public ArenaKind ArenaKindShown => _arenaKind;

    public bool ArenaTabRegular { get => _arenaKind == ArenaKind.Arena; set => SetArenaKind(value, ArenaKind.Arena); }

    public bool ArenaTabUnderground { get => _arenaKind == ArenaKind.Underground; set => SetArenaKind(value, ArenaKind.Underground); }

    /// <summary>The community class win rates have no Underground split: the Underground tab says "no data" instead.</summary>
    public bool ArenaCommunityNoSplit => _arenaKind == ArenaKind.Underground;

    /// <summary>
    /// The Arena numbers that fit <paramref name="kind"/>: Underground never shows the class numbers (regular Arena only);
    /// card scores of the other Arena's slice are not used for a deck score (an unknown slice counts as the regular one).
    /// </summary>
    internal static ArenaMeta? ArenaMetaFor(ArenaMeta? meta, ArenaKind kind)
    {
        if (meta is null)
        {
            return null;
        }

        var cardsFit = (meta.Underground ?? false) == (kind == ArenaKind.Underground);
        return (kind, cardsFit) switch
        {
            (ArenaKind.Underground, true) => meta.WithoutClasses(),
            (ArenaKind.Underground, false) => ArenaMeta.Empty,
            (_, true) => meta,
            _ => new ArenaMeta([], meta.Classes, meta.FetchedAt, meta.Underground),
        };
    }

    private void SetArenaKind(bool chosen, ArenaKind kind)
    {
        if (!chosen || _arenaKind == kind)
        {
            return;
        }

        _arenaKind = kind;
        Raise(nameof(ArenaTabRegular));
        Raise(nameof(ArenaTabUnderground));
        Raise(nameof(ArenaKindShown));
        Raise(nameof(ArenaCommunityNoSplit));
        RaiseArenaCommunityStates();
        RebuildArenaStats();
    }
}
