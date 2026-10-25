using Avalonia;
using Avalonia.Threading;

namespace Hearthpulse.UI.Overlay;

/// <summary>Platform values consumed by this host's overlay bindings and dispatcher.</summary>
public sealed partial class OverlayViewModel
{
    private double _winShare;
    private double _tieShare;
    private double _lossShare;

    /// <summary>Win / tie / loss shares (0..1) for the odds bar.</summary>
    public double WinShare { get => _winShare; private set => Set(ref _winShare, value); }
    public double TieShare { get => _tieShare; private set => Set(ref _tieShare, value); }
    public double LossShare { get => _lossShare; private set => Set(ref _lossShare, value); }

    private void SetOddsShares(double win, double tie, double loss)
    {
        WinShare = win;
        TieShare = tie;
        LossShare = loss;
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
