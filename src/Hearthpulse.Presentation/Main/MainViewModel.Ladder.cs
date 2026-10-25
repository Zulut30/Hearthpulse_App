using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>The ladder session card on the Overview page (the overlay shows the same in the game's menus).</summary>
public sealed partial class MainViewModel
{
    private bool _showLadder;
    private string _ladderSince = string.Empty;
    private IReadOnlyList<LadderSessionLine> _ladderLines = [];
    private string _ladderKey = string.Empty;

    public bool ShowLadder { get => _showLadder; private set => Set(ref _showLadder, value); }

    public string LadderSince { get => _ladderSince; private set => Set(ref _ladderSince, value); }

    public IReadOnlyList<LadderSessionLine> LadderLines { get => _ladderLines; private set => Set(ref _ladderLines, value); }

    private void ApplyLadder(LiveSnapshot s)
    {
        var ladder = s.Ladder;
        ShowLadder = Settings.ShowLadderSession && ladder.Any;
        if (!ShowLadder)
        {
            return;
        }

        var lines = LadderSessionLine.Of(ladder);
        var key = Loc.Instance.Language + string.Join('|', lines);
        if (key != _ladderKey)
        {
            _ladderKey = key;
            LadderLines = lines;
        }

        LadderSince = Loc.F("Widgets.Ladder.Since", (ladder.Standard ?? ladder.Wild)!.Since);
    }
}
