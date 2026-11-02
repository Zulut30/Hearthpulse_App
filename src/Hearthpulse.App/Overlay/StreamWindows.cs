using System.Windows;
using Hearthpulse.App.Main;
using Hearthpulse.Core;
using Forms = System.Windows.Forms;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Keeps the OBS capture windows (docs/streaming.md) in step with Settings → Stream: opens the ones switched on at their
/// saved place, closes the others, repaints them, saves where they were moved. The host calls <see cref="Sync"/> after a
/// stream setting changed and <see cref="Recreate"/> after a theme change (WPF windows take their colours when built).
/// </summary>
public sealed class StreamWindows
{
    private readonly OverlayViewModel _overlay;
    private readonly MainViewModel _main;
    private readonly Dictionary<string, StreamWindow> _open = new(StringComparer.Ordinal);

    public StreamWindows(OverlayViewModel overlay, MainViewModel main)
    {
        _overlay = overlay;
        _main = main;
    }

    private AppSettings Settings => _main.Settings;

    /// <summary>Raised when a window was moved or resized (its place is in the settings now): the host saves.</summary>
    public event Action? Moved;

    /// <summary>The open window with this id, if any (render scenes and tests).</summary>
    public StreamWindow? Window(string id) => _open.GetValueOrDefault(id);

    /// <summary>Opens the windows switched on, closes the ones switched off, and applies background and "on top" to all.</summary>
    public void Sync()
    {
        foreach (var spec in StreamWindowSpec.All)
        {
            var open = _open.GetValueOrDefault(spec.Id);
            if (spec.IsOn(Settings) && open is null)
            {
                Open(spec, show: true);
            }
            else if (!spec.IsOn(Settings) && open is not null)
            {
                _open.Remove(spec.Id);
                open.CloseForReal();
            }
            else
            {
                open?.ApplyLook();
            }
        }
    }

    /// <summary>Builds the open windows again in the new theme, where they were.</summary>
    public void Recreate()
    {
        foreach (var old in _open.Values.ToList())
        {
            Keep(old.Spec);
            _open.Remove(old.Spec.Id);
            old.CloseForReal();
            Open(old.Spec, show: true);
        }
    }

    /// <summary>A window built at its place but not shown (off-screen renders).</summary>
    public StreamWindow Build(StreamWindowSpec spec) => Create(spec);

    private void Open(StreamWindowSpec spec, bool show)
    {
        var window = Create(spec);
        _open[spec.Id] = window;
        if (show)
        {
            window.Show();
        }
    }

    private StreamWindow Create(StreamWindowSpec spec)
    {
        var window = new StreamWindow(_overlay, Settings, spec)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var place = StreamWindowLayout.Place(spec, Settings.StreamWindows.GetValueOrDefault(spec.Id), WorkAreas(), Primary());
        window.Left = place.X;
        window.Top = place.Y;
        window.Width = place.Width;
        window.Height = place.Height;
        window.Placed += s =>
        {
            Keep(s);
            Moved?.Invoke();
        };
        window.OnTopToggled += () => _main.StreamWindowsOnTop = !Settings.StreamWindowsOnTop;
        window.CloseRequested += s =>
        {
            // Closed by hand: switched off (the switch in Settings follows). A window already replaced (theme) says nothing.
            if (!ReferenceEquals(_open.GetValueOrDefault(s.Id), window))
            {
                return;
            }

            Keep(s);
            _open.Remove(s.Id);
            window.CloseForReal();
            _main.SetStreamWindow(s.Id, false);
        };
        return window;
    }

    private void Keep(StreamWindowSpec spec)
    {
        if (_open.GetValueOrDefault(spec.Id)?.Place is { } place)
        {
            StreamWindowLayout.Save(Settings, spec, place);
        }
    }

    /// <summary>The primary screen's work area in device-independent pixels.</summary>
    private static StreamRect Primary()
    {
        var area = SystemParameters.WorkArea;
        return new StreamRect(area.Left, area.Top, area.Width, area.Height);
    }

    /// <summary>
    /// Every screen's work area in device-independent pixels, scaled by the primary screen's factor (mixed scaling between
    /// monitors makes this approximate, which is enough to tell whether a saved window still shows somewhere).
    /// </summary>
    private static List<StreamRect> WorkAreas()
    {
        var primary = Forms.Screen.PrimaryScreen?.WorkingArea;
        var scale = primary is { Width: > 0 } p ? SystemParameters.WorkArea.Width / p.Width : 1;
        return Forms.Screen.AllScreens
            .Select(s => new StreamRect(s.WorkingArea.Left * scale, s.WorkingArea.Top * scale, s.WorkingArea.Width * scale, s.WorkingArea.Height * scale))
            .ToList();
    }
}
