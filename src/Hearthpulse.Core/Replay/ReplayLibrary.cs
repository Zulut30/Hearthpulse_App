using System.Collections.Concurrent;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Replay;

/// <summary>Which folder of the library a replay is in.</summary>
public enum ReplayShelf
{
    /// <summary>The data folder's replays of History games (trimmed by <see cref="ReplayStore.Prune"/>).</summary>
    Recent,

    /// <summary>Starred copies, never trimmed.</summary>
    Saved,

    /// <summary>Imported replays: the last <see cref="ReplayLibrary.MaxImportedFiles"/> / <see cref="ReplayLibrary.MaxImportedBytes"/>.</summary>
    Imported,
}

/// <summary>
/// The replay library (<c>docs/replay-library.md</c>): History's replays, the starred copies (Saved) and the imported
/// games, the last two outside the data folder (<see cref="AppPaths.LibraryRoot"/>). A star copies a file into Saved
/// under its own name (the History row keeps its file, the copy outlives the 300 MB trim); a History game whose file was
/// trimmed opens from that copy. Headers are read once per file and kept.
/// </summary>
public sealed class ReplayLibrary(ReplayStore recent, string saved, string imported)
{
    public const int MaxImportedFiles = 500;
    public const long MaxImportedBytes = 200L * 1024 * 1024;

    /// <summary>Headers by file and the time it was written: a file trimmed away or written again is read afresh.</summary>
    private readonly ConcurrentDictionary<(ReplayShelf, string, DateTime), ReplayHeader?> _headers = new();

    public static ReplayLibrary Default { get; } = new(ReplayStore.Default, AppPaths.SavedReplays, AppPaths.ImportedReplays);

    public ReplayStore Recent { get; } = recent;

    public ReplayStore Saved { get; } = new(saved) { AutoPrune = false, MaxTotalBytes = long.MaxValue, MaxTotalFiles = int.MaxValue };

    public ReplayStore Imported { get; } = new(imported) { MaxTotalBytes = MaxImportedBytes, MaxTotalFiles = MaxImportedFiles, PruneByWriteTime = true };

    public ReplayStore Store(ReplayShelf shelf) => shelf switch
    {
        ReplayShelf.Saved => Saved,
        ReplayShelf.Imported => Imported,
        _ => Recent,
    };

    /// <summary>The names of the starred copies (a star is lit by name).</summary>
    public IReadOnlySet<string> SavedNames() => Saved.Names().ToHashSet(StringComparer.Ordinal);

    /// <summary>The header of a file (summary for the card), read once and kept; null when it cannot be read.</summary>
    public ReplayHeader? Header(ReplayShelf shelf, string? fileName) =>
        fileName is null || Store(shelf).WrittenAt(fileName) is not { } written
            ? null
            : _headers.GetOrAdd((shelf, fileName, written), static (key, library) => library.Store(key.Item1).ReadHeader(key.Item2), this);

    /// <summary>The text of a replay: from its folder, else from its starred copy (a trimmed History file).</summary>
    public string? Load(ReplayShelf shelf, string? fileName) =>
        fileName is null ? null : Store(shelf).Load(fileName) ?? (shelf == ReplayShelf.Saved ? null : Saved.Load(fileName));

    /// <summary>Stars a replay: copies it into Saved. True when the copy is there.</summary>
    public bool Star(ReplayShelf from, string fileName) => from != ReplayShelf.Saved && Saved.CopyFrom(Store(from), fileName);

    /// <summary>True when the starred copy is the only file of this replay left (unstarring then deletes it for good).</summary>
    public bool IsLastCopy(string fileName) => !Recent.Contains(fileName) && !Imported.Contains(fileName);

    /// <summary>Removes the star: deletes the copy in Saved.</summary>
    public bool Unstar(string fileName) => Saved.Delete(fileName);

    /// <summary>Deletes an imported game (its starred copy, if any, stays).</summary>
    public bool DeleteImported(string fileName) => Imported.Delete(fileName);

    /// <summary>Imports a file (a replay or a Power.log) into Imported, see <see cref="ReplayImport"/>.</summary>
    public ReplayImportResult Import(string path, CardDb cards, CancellationToken cancellationToken = default) =>
        ReplayImport.ImportFile(path, Imported, cards, Path.Combine(AppPaths.Cache, "import"), cancellationToken);

    /// <summary>The copy to share of a replay (<see cref="ReplayShare"/>): its path, or null.</summary>
    public string? ShareCopy(ReplayShelf shelf, string fileName, CardDb cards, string? shareRoot = null) =>
        Load(shelf, fileName) is { } text ? ReplayShare.Prepare(text, shareRoot ?? AppPaths.ShareTemp, cards) : null;
}
