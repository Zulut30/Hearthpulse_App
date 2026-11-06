using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main;

public sealed partial record PlacementBar
{
    public bool First => Place == 1;
}

public sealed partial class MainViewModel
{
    private static int CountHistory(IReadOnlyList<GameRecord> recent) => recent.Count(g => !g.IsAbandoned);

    private static bool UseTribeRows(BattlegroundsMeta meta) => false;
}
