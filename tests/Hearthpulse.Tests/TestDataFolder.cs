using System.Runtime.CompilerServices;
using Hearthpulse.Core;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// Points <see cref="AppPaths"/> at a scratch folder before any test touches it. Without this the tests wrote into the
/// user's real data folder (%APPDATA%\Hearthpulse): fake "Update available" lines in hearthpulse.log, staged updates,
/// cached meta files. The folder is deleted when the test run ends.
/// </summary>
internal static class TestDataFolder
{
    public static string Path { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hearthpulse-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        // Always overridden, also when the developer's shell already sets it: tests never write to a real folder.
        Environment.SetEnvironmentVariable("HEARTHPULSE_DATA", Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete();
    }

    private static void Delete()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still open at exit: the folder is in the temp directory, the OS cleans it up eventually.
        }
    }
}

public sealed class TestDataFolderTests
{
    [Fact]
    public void App_data_goes_to_the_scratch_folder()
    {
        Assert.StartsWith(TestDataFolder.Path, AppPaths.Root);
        Assert.StartsWith(TestDataFolder.Path, AppPaths.LogFile);
    }
}
