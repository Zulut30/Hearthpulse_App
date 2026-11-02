using System.Runtime.CompilerServices;
using Hearthpulse.Core.Hosting;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Points <c>AppPaths</c> at a scratch folder before any test touches it, so these tests never read or write the
/// user's real data folder (%APPDATA%\Hearthpulse), and gives the main view models of these tests that folder's data
/// and replay stores. The folder is deleted when the test run ends.
/// </summary>
internal static class TestApp
{
    private static readonly Lazy<AppData> LazyData = new(AppData.FromAppPaths);

    public static string Folder { get; private set; } = string.Empty;

    public static AppData Data => LazyData.Value;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Folder = Path.Combine(Path.GetTempPath(), $"hearthpulse-presentation-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Folder);
        // Always overridden, also when the developer's shell already sets it: tests never write to a real folder.
        Environment.SetEnvironmentVariable("HEARTHPULSE_DATA", Folder);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete();
    }

    private static void Delete()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open at exit: the folder is in the temp directory, the OS cleans it up eventually.
        }
    }
}
