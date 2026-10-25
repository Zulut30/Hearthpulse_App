using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.UI.Overlay;

/// <summary>The "ladder-session" panel: the ranked session in the game's menus, Standard and Wild apart.</summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Scenes of other modes, where a ranked widget is out of place.</summary>
    private static readonly HashSet<string> NotLadderScenes = ["BACON", "DRAFT", "LETTUCE_MAP", "LETTUCE_PLAY", "LETTUCE_VILLAGE", "LETTUCE_BOUNTY_BOARD"];

    private bool _showLadder;
    private string _ladderSince = string.Empty;
    private IReadOnlyList<LadderSessionLine> _ladderLines = [];

    public bool ShowLadder { get => _showLadder; private set => Set(ref _showLadder, value); }

    public string LadderSince { get => _ladderSince; private set => Set(ref _ladderSince, value); }

    public IReadOnlyList<LadderSessionLine> LadderLines { get => _ladderLines; private set => Set(ref _ladderLines, value); }

    private void UpdateLadder(LiveSnapshot snapshot, AppSettings settings)
    {
        var ladder = snapshot.Ladder;
        ShowLadder = settings.OverlayEnabled && settings.ShowLadderSession && snapshot.HearthstoneRunning && !snapshot.InGame
                     && !NotLadderScenes.Contains(snapshot.Scene) && ladder.Any;
        if (!ShowLadder)
        {
            return;
        }

        var lines = LadderSessionLine.Of(ladder);
        Replace(nameof(LadderLines), lines, l => l.ToString(), () => LadderLines = lines);
        var since = (ladder.Standard ?? ladder.Wild)!.Since;
        LadderSince = Loc.F("Widgets.Ladder.Since", since);
    }
}
