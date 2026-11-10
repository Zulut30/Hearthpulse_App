using Hearthpulse.Core.Data;

namespace Hearthpulse.UI.Main;

public sealed partial class MainViewModel
{
    private static int CountHistory(IReadOnlyList<GameRecord> recent) => recent.Count(g => !g.IsAbandoned);
}
