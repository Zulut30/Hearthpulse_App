using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthlens.Core;
using Hearthlens.Desktop.Platform;
using Hearthlens.UI;
using Hearthlens.UI.Overlay;

namespace Hearthlens.Desktop;

/// <summary>
/// Transparent, click-through, topmost window glued to Hearthstone's client area. The platform layer finds the game
/// and toggles click-through; panels marked interactive take the mouse while the pointer is over them.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly AppHost _host;
    private readonly IOverlayPlatform _platform;
    private readonly OverlayView _view;
    private readonly DispatcherTimer _timer;
    private PixelRect? _lastArea;
    private bool _shown;
    private int _hoverReadBusy;
    private DateTime _nextHoverRead;
    private volatile string? _memoryHover;

    public OverlayWindow(AppHost host, IOverlayPlatform platform)
    {
        _host = host;
        _platform = platform;
        Title = "Hearthlens Overlay";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Focusable = false;
        Width = 1;
        Height = 1;

        _view = new OverlayView { DataContext = host.Overlay, Settings = host.Settings, IsVisible = false };
        _view.LayoutChanged += host.OnSettingsChanged;
        Content = _view;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        Opened += (_, _) =>
        {
            _platform.Attach(this);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Temporarily hides the overlay (hotkey or tray) without changing settings.</summary>
    public bool Suppressed { get; set; }

    private void Tick()
    {
        var vm = _host.Overlay;
        var area = _platform.FindGameClientArea();
        // Layout mode stays up while the player arranges panels from the dashboard as well.
        var visible = area is not null && !Suppressed
                      && (vm.LayoutEditing || (_host.Settings.OverlayEnabled && _platform.IsGameForeground()));
        if (!visible)
        {
            if (_shown)
            {
                _view.IsVisible = false;
                _shown = false;
                vm.HoverLeaderboard(0, 0, 0);
            }

            _platform.SetClickThrough(true);
            return;
        }

        var r = area!.Value;
        if (!_shown)
        {
            _view.IsVisible = true;
            _shown = true;
        }

        // Window positions are in screen pixels (points on macOS); sizes are device-independent.
        var scaling = DesktopScaling;
        if (r != _lastArea)
        {
            _lastArea = r;
            Position = r.Position;
            Width = r.Width / scaling;
            Height = r.Height / scaling;
        }

        _view.Fit(r.Width / scaling, r.Height / scaling);

        Point? local = null;
        if (_platform.PointerPosition() is { } p && r.Contains(p))
        {
            local = new Point((p.X - r.X) / scaling, (p.Y - r.Y) / scaling);
        }

        // In layout mode the whole overlay takes the mouse so panels can be dragged anywhere.
        var interactive = local is { } point && (vm.LayoutEditing || _view.IsInteractiveAt(point));
        _platform.SetClickThrough(!interactive);

        PollHoveredHero();
        _view.UpdateLeaderboardHover(local, _memoryHover);
    }

    /// <summary>Reads the hovered leaderboard hero from the game (Windows memory reader) at most ~10 times a second.</summary>
    private void PollHoveredHero()
    {
        var memory = _host.Memory;
        var now = DateTime.UtcNow;
        if (!memory.IsAttached || now < _nextHoverRead || Interlocked.Exchange(ref _hoverReadBusy, 1) == 1)
        {
            if (!memory.IsAttached)
            {
                _memoryHover = null;
            }

            return;
        }

        _nextHoverRead = now.AddMilliseconds(90);
        Task.Run(() =>
        {
            try
            {
                _memoryHover = memory.GetHoveredLeaderboardHero();
            }
            catch (Exception ex)
            {
                _memoryHover = null;
                Diag.Error("leaderboard hover read failed", ex);
            }
            finally
            {
                Volatile.Write(ref _hoverReadBusy, 0);
            }
        });
    }
}
