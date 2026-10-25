using System.Windows;
using System.Windows.Threading;

namespace Hearthpulse.App.Overlay;

/// <summary>Platform values consumed by this host's overlay bindings and dispatcher.</summary>
public sealed partial class OverlayViewModel
{
    private GridLength _winShare = new(0, GridUnitType.Star);
    private GridLength _tieShare = new(0, GridUnitType.Star);
    private GridLength _lossShare = new(0, GridUnitType.Star);

    public GridLength WinShare { get => _winShare; private set => Set(ref _winShare, value); }
    public GridLength TieShare { get => _tieShare; private set => Set(ref _tieShare, value); }
    public GridLength LossShare { get => _lossShare; private set => Set(ref _lossShare, value); }

    private void SetOddsShares(double win, double tie, double loss)
    {
        WinShare = new GridLength(win, GridUnitType.Star);
        TieShare = new GridLength(tie, GridUnitType.Star);
        LossShare = new GridLength(loss, GridUnitType.Star);
    }

    private static Thickness HandOffset(double x, double y) => new(x, y, 0, 0);

    private DispatcherTimer? _turnTick;

    private DispatcherTimer NewTurnTick()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshTurnTimer();
        return timer;
    }
}
