using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Tracking.Counters;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    /// <summary>The Battlegrounds counters with their modes (set through <see cref="SetCounterMode"/>, like the Constructed ones).</summary>
    public IReadOnlyList<CounterSettingRow> BgCounterRows => BattlegroundsCounters.All
        .Select(c =>
        {
            var mode = Counters.ModeOf(Settings.CounterModes, c.Id);
            return new CounterSettingRow(c.Id, c.Title, c.Description, mode == CounterMode.Auto, mode == CounterMode.Always, mode == CounterMode.Off);
        })
        .ToList();
}
