using Hearthpulse.Core.Replay;

namespace Hearthpulse.Core.Hosting;

/// <summary>
/// The data folder and the replay files kept in it and beside it (History's replays, the Saved and Imported library
/// folders, the record of uploads): built once by <see cref="AppServices"/> and passed to whoever needs them, instead of
/// each reading a static store of its own.
/// </summary>
public sealed class AppData
{
    public AppData(string root, ReplayStore replays, ReplayLibrary library, ReplayUploads uploads)
    {
        Root = root;
        Replays = replays;
        Library = library;
        Uploads = uploads;
    }

    /// <summary>The data folder (settings, history, caches, replays).</summary>
    public string Root { get; }

    /// <summary>History's replays: one file per game, trimmed by size and count.</summary>
    public ReplayStore Replays { get; }

    /// <summary>The replay library: History's replays (<see cref="Replays"/>), Saved and Imported.</summary>
    public ReplayLibrary Library { get; }

    /// <summary>Replays uploaded to the site, by file name (plan 2.2).</summary>
    public ReplayUploads Uploads { get; }

    /// <summary>The app's own folders, as <see cref="AppPaths"/> resolves them (<c>HEARTHPULSE_DATA</c>, portable, the user's).</summary>
    public static AppData FromAppPaths()
    {
        var replays = new ReplayStore(AppPaths.Replays);
        return new AppData(AppPaths.Root, replays, new ReplayLibrary(replays, AppPaths.SavedReplays, AppPaths.ImportedReplays),
            new ReplayUploads(Path.Combine(AppPaths.Replays, "uploads.json")));
    }
}
