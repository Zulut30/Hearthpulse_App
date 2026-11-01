namespace Hearthpulse.Core.Migration;

/// <summary>The tracker a migration reads.</summary>
public enum MigrationSource
{
    Hdt,
    Firestone,
}

/// <summary>
/// What was found before importing: a source, its folder or file, how many finished games and library decks it holds.
/// <see cref="ServerOnly"/>: Firestone is installed but keeps its history on its server only (nothing to read here).
/// <see cref="Counted"/> false: a history found but not counted (a database the search does not open), counted by the import.
/// </summary>
public sealed record MigrationFound(MigrationSource Source, string Path, int Games, int Decks, bool ServerOnly = false, bool Counted = true)
{
    /// <summary>There is something to bring over (or, uncounted, may be).</summary>
    public bool HasWork => !ServerOnly && (!Counted || Games + Decks > 0);
}

/// <summary>
/// What an import did: games added to History, games History already had, games left out (unfinished, Mercenaries,
/// spectated, unreadable), decks saved, replays rebuilt from the tracker's own replay files; or why it stopped.
/// </summary>
public sealed record MigrationResult(int Added, int Already, int Skipped, int Decks, int Replays, string? Error = null)
{
    public static MigrationResult Failed(string error) => new(0, 0, 0, 0, 0, error);
}

/// <summary>Import progress: games handled so far of the total.</summary>
public readonly record struct MigrationProgress(int Done, int Total);
