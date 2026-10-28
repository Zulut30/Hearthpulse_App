using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>Marks on the opponent's hand fan, one on the visible edge of each card.</summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Width of the opponent-hand panel in overlay units; it hangs from the top of the window, centred on it.</summary>
    public const double HandPanelWidth = 640;

    /// <summary>Height of the opponent-hand panel: the deepest card edge plus a mark with a portrait.</summary>
    public const double HandPanelHeight = 112;

    /// <summary>Width of one mark: narrower than the pitch of a full ten-card fan, so neighbours never touch.</summary>
    public const double HandMarkWidth = 28;

    /// <summary>How far a mark's top sits above its card's edge, so it overlaps the card instead of hanging below it.</summary>
    private const double HandMarkLift = 10;

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
        // The fan is measured in units of a 1080-high window; the overlay scale makes the surface smaller or larger than that.
        var scale = Math.Clamp(settings.OverlayScale, 0.6, 1.6);
        var key = $"{scale}|" + string.Join('|', hand.Select(h => $"{h.EntityId}:{h.Turn}:{h.CardId}:{h.CreatedById}:{h.Origin}"));
        if (key == _handMarksKey)
        {
            return;
        }

        _handMarksKey = key;
        var positions = OpponentHandFan.Positions(hand.Count);
        HandMarks = hand
            .Select((h, i) => new HandMark(
                HandOffset((HandPanelWidth / 2) + (positions[i].X / scale) - (HandMarkWidth / 2), Math.Max(0, (positions[i].Y - HandMarkLift) / scale)),
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
