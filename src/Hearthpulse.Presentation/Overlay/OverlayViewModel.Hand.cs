using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>Marks under the opponent's hand fan.</summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Width of the opponent-hand panel in overlay units; the fan centre is its middle.</summary>
    public const double HandPanelWidth = 640;

    /// <summary>Width of one mark.</summary>
    public const double HandMarkWidth = 40;

    private bool _showHandMarks;
    private IReadOnlyList<HandMark> _handMarks = Array.Empty<HandMark>();
    private string _handMarksKey = string.Empty;

    public bool ShowHandMarks { get => _showHandMarks; private set => Set(ref _showHandMarks, value); }

    public IReadOnlyList<HandMark> HandMarks { get => _handMarks; private set => Set(ref _handMarks, value); }

    private void UpdateHandMarks(ConstructedView? view, AppSettings settings)
    {
        // During the mulligan the opponent's cards are not in the fan yet.
        var hand = view is { Turn: > 0 } ? view.OpponentHand : [];
        ShowHandMarks = settings.ShowOpponentTracker && hand.Count > 0;
        var key = string.Join('|', hand.Select(h => $"{h.EntityId}:{h.Turn}:{h.CardId}:{h.CreatedById}:{h.Origin}"));
        if (key == _handMarksKey)
        {
            return;
        }

        _handMarksKey = key;
        var positions = OpponentHandFan.Positions(hand.Count);
        HandMarks = hand
            .Select((h, i) => new HandMark(
                HandOffset((HandPanelWidth / 2) + positions[i].X - (HandMarkWidth / 2), positions[i].Y),
                OpponentHandFan.Label(h),
                h.CardId ?? h.CreatedById,
                h.CardId is not null && !h.IsCoin,
                h.CardId is null && h.CreatedById is not null,
                h.IsCoin,
                h.CardId is { } id && !h.IsCoin ? id : h.CreatedById,
                OpponentHandFan.Hint(h)))
            .ToList();
    }
}
