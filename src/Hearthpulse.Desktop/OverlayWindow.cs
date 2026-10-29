using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Desktop.Platform;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// Transparent, click-through, topmost window glued to Hearthstone's client area. The platform layer finds the game
/// and toggles click-through; panels marked interactive take the mouse while the pointer is over them.
/// </summary>
internal sealed class OverlayWindow : Window
{
    /// <summary>Overlay tick while the game window is there (follow it, pointer, click-through) and while it is not.</summary>
    private static readonly TimeSpan ActiveTick = TimeSpan.FromMilliseconds(33);

    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(500);

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
        SetTitle();
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

        _timer = new DispatcherTimer(ActiveTick, DispatcherPriority.Render, (_, _) => Tick());
        Opened += (_, _) =>
        {
            _platform.Attach(this);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Loc.Instance.LanguageChanged -= SetTitle;
        };
        Loc.Instance.LanguageChanged += SetTitle;
    }

    private void SetTitle() => Title = Loc.T("Pool.OverlayWindow.Title");

    /// <summary>Temporarily hides the overlay (hotkey or tray) without changing settings.</summary>
    public bool Suppressed { get; set; }

    private string? _visibilityNote;

    /// <summary>
    /// One log line whenever the overlay starts or stops showing, with the reason, so "I did not see the panel" can be
    /// told apart in hearthpulse.log from a hidden overlay (another window in front, the hotkey, the setting).
    /// </summary>
    private void NoteVisibility(bool visible, PixelRect? area, bool layout)
    {
        var note = visible ? layout ? "shown (layout mode)" : $"shown over Hearthstone {area!.Value.Width}x{area.Value.Height}"
            : area is null ? "hidden: no Hearthstone window"
            : Suppressed ? "hidden with the hotkey"
            : !_host.Settings.OverlayEnabled ? "hidden: the overlay is off in Settings"
            : "hidden: Hearthstone is not the foreground window";
        if (note != _visibilityNote)
        {
            _visibilityNote = note;
            Diag.Info($"Overlay {note}");
        }
    }

    private void Tick()
    {
        var vm = _host.Overlay;
        var area = _platform.FindGameClientArea();
        // Without a game window there is nothing to draw or follow: tick twice a second instead of thirty times.
        var interval = area is null ? IdleTick : ActiveTick;
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }

        // Layout mode stays up while the player arranges panels from the dashboard as well.
        var visible = area is not null && !Suppressed
                      && (vm.LayoutEditing || (_host.Settings.OverlayEnabled && _platform.IsGameForeground()));
        NoteVisibility(visible, area, vm.LayoutEditing);
        if (!visible)
        {
            if (_shown)
            {
                _view.IsVisible = false;
                _shown = false;
                vm.HoverLeaderboard(0, 0, 0);
            }

            _platform.SetClickThrough(true);
            _view.UpdateCardPoolPointer(null, default);
            return;
        }

        var r = area!.Value;
        if (!_shown)
        {
            _view.IsVisible = true;
            _shown = true;
            _view.OfferMoveTip();
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

        // In layout mode the whole overlay takes the mouse so panels can be dragged anywhere; during a game only a panel
        // under the pointer with Ctrl held does. A panel being dragged or resized keeps it wherever the pointer goes.
        var armed = _view.UpdateDragArm(local, _platform.ControlDown());
        var interactive = armed || (local is { } point && (vm.LayoutEditing || _view.IsInteractiveAt(point)));
        _platform.SetClickThrough(!interactive);
        if (!interactive)
        {
            _view.HidePreview();
        }

        // The game is asked only in Battlegrounds, with opponent boards on and the pointer near the leaderboard.
        if (_view.WantsLeaderboardHero(local))
        {
            PollHoveredHero();
        }
        else
        {
            _memoryHover = null;
        }

        _view.UpdateLeaderboardHover(local, _memoryHover);
        _view.UpdateCardPoolPointer(local, _platform.MouseButtons());
    }

    /// <summary>Reads the hovered leaderboard hero from the game (Windows memory reader) at most four times a second.</summary>
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

        _nextHoverRead = now.AddMilliseconds(250);
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
