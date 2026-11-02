using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// Keeps the OBS capture windows (docs/streaming.md) in step with Settings → Stream: opens the ones switched on at their
/// saved place, closes the others, repaints them, saves where they were moved. Every stream switch (Settings, the live
/// bar, the tray, a window's own menu) goes through <see cref="UI.Main.MainViewModel.StreamSettingsChanged"/>, which
/// lands here. A theme change repaints the shared brushes in place, so nothing is rebuilt.
/// </summary>
internal sealed class StreamWindows
{
    private readonly AppHost _host;
    private readonly Dictionary<string, StreamWindow> _open = new(StringComparer.Ordinal);
    private Window? _screens;

    public StreamWindows(AppHost host)
    {
        _host = host;
    }

    private AppSettings Settings => _host.Settings;

    /// <summary>Any window that can tell the screens apart (the first one opened is fine).</summary>
    public void UseScreensOf(Window window) => _screens ??= window;

    /// <summary>Opens the windows switched on, closes the ones switched off, and applies background and "on top" to all.</summary>
    public void Sync()
    {
        foreach (var spec in StreamWindowSpec.All)
        {
            var open = _open.GetValueOrDefault(spec.Id);
            if (spec.IsOn(Settings) && open is null)
            {
                Open(spec);
            }
            else if (!spec.IsOn(Settings) && open is not null)
            {
                Keep(open);
                _open.Remove(spec.Id);
                open.CloseForReal();
            }
            else
            {
                open?.ApplyLook();
            }
        }
    }

    /// <summary>Closes every window, keeping them switched on for the next start.</summary>
    public void CloseAll()
    {
        foreach (var window in _open.Values.Where(w => w.IsVisible).ToList())
        {
            Keep(window);
            window.CloseForReal();
        }

        _open.Clear();
    }

    private void Open(StreamWindowSpec spec)
    {
        var window = new StreamWindow(_host.Overlay, Settings, spec);
        _open[spec.Id] = window;
        var screens = (_screens ?? window).Screens;
        var all = screens.All;
        var primary = all.FirstOrDefault(s => s.IsPrimary) ?? all.FirstOrDefault();
        var scaling = primary?.Scaling ?? 1;
        var areas = all.Select(s => new StreamRect(s.WorkingArea.X / s.Scaling, s.WorkingArea.Y / s.Scaling, s.WorkingArea.Width / s.Scaling, s.WorkingArea.Height / s.Scaling)).ToList();
        var main = primary is null ? new StreamRect(0, 0, 1280, 720)
            : new StreamRect(primary.WorkingArea.X / scaling, primary.WorkingArea.Y / scaling, primary.WorkingArea.Width / scaling, primary.WorkingArea.Height / scaling);
        var place = StreamWindowLayout.Place(spec, Settings.StreamWindows.GetValueOrDefault(spec.Id), areas, main);
        window.MoveTo(place, scaling);
        window.Placed += _ =>
        {
            Keep(window);
            _host.OnSettingsChanged();
        };
        window.OnTopToggled += () => _host.Main.StreamWindowsOnTop = !Settings.StreamWindowsOnTop;
        window.CloseRequested += s =>
        {
            // Closed by hand: switched off (the switch in Settings follows), which closes it in Sync.
            if (ReferenceEquals(_open.GetValueOrDefault(s.Id), window))
            {
                _host.Main.SetStreamWindow(s.Id, false);
            }
        };
        window.Show();
        UseScreensOf(window);
    }

    private void Keep(StreamWindow window)
    {
        if (window.Place is { } place)
        {
            StreamWindowLayout.Save(Settings, window.Spec, place);
        }
    }
}
