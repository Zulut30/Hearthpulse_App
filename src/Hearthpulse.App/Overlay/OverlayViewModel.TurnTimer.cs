using System.Windows.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App.Overlay;

/// <summary>The "turn-timer" panels by the hero portraits: the running turn's time, the rope warning, the average turn.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showTurnTimer;
    private TurnTimerSide _myTimer = TurnTimerSide.Idle;
    private TurnTimerSide _opponentTimer = TurnTimerSide.Idle;
    private TurnClockView? _turnView;
    private DispatcherTimer? _turnTick;

    public bool ShowTurnTimer { get => _showTurnTimer; private set => Set(ref _showTurnTimer, value); }

    public TurnTimerSide MyTimer { get => _myTimer; private set => Set(ref _myTimer, value); }

    public TurnTimerSide OpponentTimer { get => _opponentTimer; private set => Set(ref _opponentTimer, value); }

    private void UpdateTurnTimer(ConstructedView? view, AppSettings settings)
    {
        _turnView = settings.ShowTurnTimer ? view?.Turns : null;
        RefreshTurnTimer();
        if (ShowTurnTimer)
        {
            _turnTick ??= NewTurnTick();
            _turnTick.Start();
        }
        else
        {
            _turnTick?.Stop();
        }
    }

    private DispatcherTimer NewTurnTick()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshTurnTimer();
        return timer;
    }

    private void RefreshTurnTimer()
    {
        var view = _turnView;
        // Log times are local wall-clock times.
        var now = DateTime.Now;
        ShowTurnTimer = view is not null && !(view.Running && view.IsStale(now));
        if (!ShowTurnTimer)
        {
            return;
        }

        MyTimer = Side(view!, now, mine: true);
        OpponentTimer = Side(view!, now, mine: false);
    }

    private static string Label(bool mine) => Loc.T(mine ? "Widgets.Turn.Mine" : "Widgets.Turn.Theirs");

    private static TurnTimerSide Side(TurnClockView view, DateTime now, bool mine)
    {
        var average = mine ? view.LocalAverage : view.OpponentAverage;
        var averageText = average is { } a ? Loc.F("Widgets.Turn.Average", TurnClockView.Format(a)) : string.Empty;
        if (view.Running && view.LocalActive == mine)
        {
            var rope = view.Rope(now);
            return new TurnTimerSide(
                Label(mine),
                true,
                rope,
                TurnClockView.Format(view.Elapsed(now)),
                rope ? Loc.F("Widgets.Turn.Rope", TurnClockView.Format(view.Remaining(now))) : Loc.F("Widgets.Turn.Of", TurnClockView.Format(TimeSpan.FromSeconds(view.LimitSeconds))),
                averageText);
        }

        var last = mine ? view.LocalLast : view.OpponentLast;
        return new TurnTimerSide(
            Label(mine),
            false,
            false,
            last is { } l ? TurnClockView.Format(l) : "—",
            Loc.T("Widgets.Turn.Last"),
            averageText);
    }
}
