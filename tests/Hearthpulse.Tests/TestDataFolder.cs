using System.Runtime.CompilerServices;
using Hearthpulse.Core;
using Hearthpulse.Core.Hosting;
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

    /// <summary>The run's data folder with its replay stores, for view models that take one (built on first use).</summary>
    public static AppData App => LazyApp.Value;

    private static readonly Lazy<AppData> LazyApp = new(AppData.FromAppPaths);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hearthpulse-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        // Always overridden, also when the developer's shell already sets it: tests never write to a real folder.
        Environment.SetEnvironmentVariable("HEARTHPULSE_DATA", Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete();
        // Avalonia's UI dispatcher belongs to the first thread that asks for it. View models post account and upload
        // events to it from pool threads; were that first, the headless platform could not start on its own thread
        // later and the test host would crash. So the headless thread is started here and takes it first (not waited
        // for: the module is still loading).
        LocBindingTests.Headless.Warm();
    }

    /// <summary>
    /// hearthpulse.log (with its rotated part) as the tests left it so far. Read with every sharing allowed: a plain
    /// File.ReadAllText locks out Diag writing from a test running in parallel, and Diag drops that line.
    /// </summary>
    public static string ReadLog()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var text = new System.Text.StringBuilder();
                foreach (var path in new[] { System.IO.Path.ChangeExtension(AppPaths.LogFile, ".old.log"), AppPaths.LogFile })
                {
                    if (File.Exists(path))
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(stream);
                        text.Append(reader.ReadToEnd());
                    }
                }

                return text.ToString();
            }
            catch (IOException) when (attempt < 20)
            {
                // The log was rotated between the existence check and the open: read again at once.
            }
        }
    }

    /// <summary>
    /// A scratch path of a test inside this run's folder: whatever <see cref="TryDelete"/> cannot remove yet goes with the
    /// folder when the run ends.
    /// </summary>
    public static string Scratch(string name) => System.IO.Path.Combine(Path, $"{name}-{Guid.NewGuid():N}");

    /// <summary>
    /// Removes a test's scratch file or folder if nothing holds it any more. A view model may still read a history file
    /// in the background for a moment; then it stays until the run's folder is deleted, and no test waits for it.
    /// </summary>
    public static void TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still held: left for the end of the run.
        }
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
