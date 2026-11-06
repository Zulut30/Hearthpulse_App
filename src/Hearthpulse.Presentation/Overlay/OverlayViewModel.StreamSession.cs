using System.Globalization;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The OBS window "Hearthpulse — Session" (proposal B1): rank or Battlegrounds rating since the stream started, the
/// change, the record or the last places. Only the player's own numbers, so streamer mode changes nothing here.
/// </summary>
public sealed partial class OverlayViewModel
{
    private StreamWindowState _sessionWindowState;
    private string _streamSessionTitle = string.Empty;
    private string _streamSessionValue = string.Empty;
    private string _streamSessionFrom = string.Empty;
    private string _streamSessionChange = string.Empty;
    private int _streamSessionChangeValue;
    private string _streamSessionRecord = string.Empty;
    private string _streamSessionSince = string.Empty;
    private bool _streamSessionEmpty = true;
    private IReadOnlyList<PlaceChip> _streamSessionPlaces = [];

    /// <summary>The "Hearthpulse — Session" window: the card whenever the window is on (with "No games yet" before the first).</summary>
    public StreamWindowState SessionWindowState { get => _sessionWindowState; private set => Set(ref _sessionWindowState, value); }

    /// <summary>"Ranked · Standard" or "Battlegrounds".</summary>
    public string StreamSessionTitle { get => _streamSessionTitle; private set => Set(ref _streamSessionTitle, value); }

    /// <summary>The rank or rating now.</summary>
    public string StreamSessionValue { get => _streamSessionValue; private set => Set(ref _streamSessionValue, value); }

    /// <summary>"from Diamond 5" / "from 6 751": where the stream started.</summary>
    public string StreamSessionFrom { get => _streamSessionFrom; private set => Set(ref _streamSessionFrom, value); }

    /// <summary>"+3 ranks", "+85", empty while nothing changed.</summary>
    public string StreamSessionChange { get => _streamSessionChange; private set => Set(ref _streamSessionChange, value); }

    /// <summary>The change's sign, for its colour.</summary>
    public int StreamSessionChangeValue { get => _streamSessionChangeValue; private set => Set(ref _streamSessionChangeValue, value); }

    /// <summary>"5–2" (Ranked); empty in Battlegrounds, which shows the places.</summary>
    public string StreamSessionRecord { get => _streamSessionRecord; private set => Set(ref _streamSessionRecord, value); }

    /// <summary>"Since 18:04".</summary>
    public string StreamSessionSince { get => _streamSessionSince; private set => Set(ref _streamSessionSince, value); }

    /// <summary>No ranked or Battlegrounds game since the start: the card says so.</summary>
    public bool StreamSessionEmpty { get => _streamSessionEmpty; private set => Set(ref _streamSessionEmpty, value); }

    /// <summary>Battlegrounds: the last places, oldest first; green in the top half.</summary>
    public IReadOnlyList<PlaceChip> StreamSessionPlaces { get => _streamSessionPlaces; private set => Set(ref _streamSessionPlaces, value); }

    public bool HasStreamSessionPlaces => _streamSessionPlaces.Count > 0;

    private void UpdateStreamSession(LiveSnapshot snapshot)
    {
        if (snapshot.StreamSession is not { } view)
        {
            SessionWindowState = StreamWindowState.Idle;
            return;
        }

        var line = StreamSessionLine.Of(view);
        StreamSessionTitle = line.Title;
        StreamSessionValue = line.Value;
        StreamSessionFrom = line.From;
        StreamSessionChange = line.Change;
        StreamSessionChangeValue = line.ChangeValue;
        StreamSessionRecord = line.Record;
        StreamSessionSince = line.Since;
        StreamSessionEmpty = view.Kind == StreamSessionKind.None;
        var top = view.Format == "Duos" ? 2 : 4;
        var places = view.Placements.Select(p => new PlaceChip(p, p <= top)).ToList();
        if (!places.SequenceEqual(_streamSessionPlaces))
        {
            StreamSessionPlaces = places;
            Raise(nameof(HasStreamSessionPlaces));
        }

        SessionWindowState = StreamWindowState.Shown;
    }
}

/// <summary>The "Session" window's texts (both UIs, tests).</summary>
public sealed record StreamSessionLine(string Title, string Value, string From, string Change, int ChangeValue, string Record, string Since)
{
    public static StreamSessionLine Of(StreamSessionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var since = Loc.F("Stream.Session.Since", view.Since);
        switch (view.Kind)
        {
            case StreamSessionKind.Ranked:
            {
                var (change, sign) = view switch
                {
                    { LegendPlaces: > 0 and var up } => (Loc.P("Widgets.Ladder.PlacesUp", up), 1),
                    { LegendPlaces: < 0 and var down } => (Loc.P("Widgets.Ladder.PlacesDown", -down), -1),
                    { Steps: > 0 and var up } => (Loc.P("Widgets.Ladder.StepsUp", up), 1),
                    { Steps: < 0 and var down } => (Loc.P("Widgets.Ladder.StepsDown", -down), -1),
                    _ => (string.Empty, 0),
                };
                var start = view.RankStart?.Display;
                var now = view.RankNow?.Display ?? "—";
                return new StreamSessionLine(
                    Loc.F("Growth.Recap.Ranked", Loc.T(view.Format == LadderSession.Wild ? "Widgets.Ladder.Wild" : "Widgets.Ladder.Standard")),
                    now,
                    start is null || start == now ? string.Empty : Loc.F("Stream.Session.From", start),
                    change,
                    sign,
                    Loc.F("Widgets.Ladder.Record", view.Record.Wins, view.Record.Losses),
                    since);
            }

            case StreamSessionKind.Battlegrounds:
            {
                var delta = view.MmrDelta ?? 0;
                return new StreamSessionLine(
                    Loc.T(view.Format == "Duos" ? "Growth.Recap.Duos" : "Growth.Recap.Battlegrounds"),
                    view.MmrNow is { } mmr ? mmr.ToString("N0", CultureInfo.CurrentCulture) : "—",
                    view.MmrStart is { } from && view.MmrNow != from ? Loc.F("Stream.Session.From", from.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty,
                    delta > 0 ? "+" + delta.ToString("N0", CultureInfo.CurrentCulture) : delta < 0 ? "−" + (-delta).ToString("N0", CultureInfo.CurrentCulture) : string.Empty,
                    Math.Sign(delta),
                    string.Empty,
                    since);
            }

            default:
                return new StreamSessionLine(Loc.T("Stream.Window.Session"), Loc.T("Stream.Session.Idle"), string.Empty, string.Empty, 0, string.Empty, since);
        }
    }
}
