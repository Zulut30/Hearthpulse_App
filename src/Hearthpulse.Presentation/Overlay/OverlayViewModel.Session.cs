using System.Globalization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The "session" panel: "MMR 6,751 → 6,779 +28" and the places of the session's games. "Now" is the same rating the
/// Battlegrounds bar reads (<c>snapshot.Rating</c>), so the two never disagree; the change is now minus the session's
/// start. Duos has no session start of its own: its rating alone, without a change.
/// </summary>
public sealed partial class OverlayViewModel
{
    private string _sessionDelta = string.Empty;
    private int _sessionDeltaValue;

    /// <summary>"+28" / "−12" since the session's start; empty without a start, at the start, and in Duos.</summary>
    public string SessionDelta { get => _sessionDelta; private set => Set(ref _sessionDelta, value); }

    /// <summary>The number behind <see cref="SessionDelta"/>, for its colour.</summary>
    public int SessionDeltaValue { get => _sessionDeltaValue; private set => Set(ref _sessionDeltaValue, value); }

    private void UpdateSession(BattlegroundsView view, LiveSnapshot snapshot)
    {
        var session = snapshot.Session;
        var now = snapshot.Rating is null ? 0 : view.IsDuos ? snapshot.Rating.Duos : snapshot.Rating.Solo;
        if (now <= 0 && !view.IsDuos)
        {
            now = session.CurrentRating ?? 0;
        }

        var start = view.IsDuos ? null : session.StartRating;
        var delta = start is { } s && now > 0 ? now - s : 0;
        SessionRating = now <= 0
            ? "—"
            : delta != 0
                ? $"{start!.Value.ToString("N0", CultureInfo.CurrentCulture)} → {now.ToString("N0", CultureInfo.CurrentCulture)}"
                : now.ToString("N0", CultureInfo.CurrentCulture);
        SessionDeltaValue = delta;
        SessionDelta = delta > 0 ? $"+{delta}" : delta < 0 ? $"−{-delta}" : string.Empty;

        var cut = view.IsDuos ? 2 : 4;
        Replace(nameof(SessionPlaces), session.Placements, p => p.ToString(CultureInfo.InvariantCulture), () =>
            SessionPlaces = session.Placements.TakeLast(12).Select(p => new PlaceChip(p, p <= cut)).ToList());
    }
}
