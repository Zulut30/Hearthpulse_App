using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.Migration;

/// <summary>How a game brought over from another tracker went into History.</summary>
internal static class MigrationRows
{
    /// <summary>
    /// Adds a game unless History has it (<see cref="HistoryStore.FindSameGame"/>). The same game twice in the other
    /// tracker's own data (a reconnect recorded as two games) keeps its longer or finished record. True when the game is
    /// new to History.
    /// </summary>
    public static bool Add(HistoryStore history, GameRecord record)
    {
        if (history.FindSameGame(record) is not { } same)
        {
            return history.Add(record);
        }

        if (same.FromOtherTracker && same.Key != record.Key && !record.IsAbandoned
            && (same.IsAbandoned || record.Duration > same.Duration))
        {
            history.ReplaceGame(same.Key, record);
        }

        return false;
    }
}

/// <summary>Finds the other trackers' data on this PC and imports one (<c>docs/migration.md</c>). Off the UI thread.</summary>
public static class MigrationScan
{
    /// <summary>Where the import's temporary copies go (replay logs, a database copy); emptied after each import.</summary>
    public static string WorkDir => Path.Combine(AppPaths.Cache, "import");

    /// <summary>HDT (the player's chosen folder, else its default one) and Firestone, each when found.</summary>
    public static IReadOnlyList<MigrationFound> Find(string? hdtFolder = null)
    {
        var found = new List<MigrationFound>();
        if (HdtImport.Scan(hdtFolder is { Length: > 0 } ? hdtFolder : HdtImport.DefaultFolder) is { } hdt)
        {
            found.Add(hdt);
        }

        if (FirestoneImport.Scan() is { } firestone)
        {
            found.Add(firestone);
        }

        return found;
    }

    /// <summary>Imports what <paramref name="found"/> points at into History (and, for HDT, the deck library and replays).</summary>
    public static MigrationResult Import(MigrationFound found, HistoryStore history, CardDb cards,
        IProgress<MigrationProgress>? progress = null, CancellationToken cancellationToken = default) => found switch
    {
        { ServerOnly: true } => MigrationResult.Failed(MigrationErrors.Missing),
        { Source: MigrationSource.Hdt } => HdtImport.Import(found.Path, history, history.Replays ?? new ReplayStore(AppPaths.Replays), cards, WorkDir, progress, cancellationToken),
        _ => FirestoneImport.Import(found.Path, history, cards, WorkDir, progress, cancellationToken),
    };
}
