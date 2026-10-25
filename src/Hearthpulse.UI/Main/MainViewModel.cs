using System.Globalization;
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
    /// <summary>Under the History title: how many games are kept, and that they stay on this device.</summary>
    public string HistorySubtitle => Loc.P("Main.History.SubtitleDevice", HistoryCount);

    private static int CountHistory(IReadOnlyList<GameRecord> recent) => recent.Count(g => !g.IsAbandoned);

    /// <summary>Tile labels are set in capitals here (the WPF app styles them instead).</summary>
    private static string TileLabel(string text) => text.ToUpper(CultureInfo.CurrentCulture);

    private static bool UseTribeRows(BattlegroundsMeta meta) => false;
}
