using Hearthpulse.Core.Hosting;
using Hearthpulse.Tests;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The data and replay stores the main view models of these tests get: those of the run's scratch data folder, which
/// <see cref="TestDataFolder"/> (linked from Hearthpulse.Tests) sets before any test touches <c>AppPaths</c>.
/// </summary>
internal static class TestApp
{
    public static AppData Data => TestDataFolder.App;
}
