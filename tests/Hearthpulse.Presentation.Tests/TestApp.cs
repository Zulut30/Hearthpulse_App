using Hearthpulse.Core.Hosting;

namespace Hearthpulse.Presentation.Tests;

/// <summary>The data folder and its replay stores the main view models of these tests use (built on first use).</summary>
internal static class TestApp
{
    private static readonly Lazy<AppData> LazyData = new(AppData.FromAppPaths);

    public static AppData Data => LazyData.Value;
}
