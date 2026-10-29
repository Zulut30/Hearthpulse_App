using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// A window's normal bounds and whether it is maximized, what WPF keeps as RestoreBounds: a window rebuilt from them
/// opens where the player had it. Avalonia reports a maximized window at the size of the screen and a minimized one at
/// (-32000, -32000), so position and size are taken only while the window is in its normal state.
/// </summary>
public sealed class WindowRestoreBounds
{
    private readonly Window _window;
    private bool _maximized;

    public WindowRestoreBounds(Window window)
    {
        _window = window;
        Note();
        window.PositionChanged += (_, _) => Note();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
            {
                // Minimized keeps what it was before: restoring brings back the maximized window.
                if (window.WindowState != WindowState.Minimized)
                {
                    _maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen;
                }
            }
            else if (e.Property == TopLevel.ClientSizeProperty || e.Property == Layoutable.WidthProperty || e.Property == Layoutable.HeightProperty)
            {
                Note();
            }
        };
    }

    /// <summary>The top-left corner in the normal state, in screen pixels.</summary>
    public PixelPoint Position { get; private set; }

    /// <summary>The client size in the normal state.</summary>
    public Size Size { get; private set; }

    /// <summary>Maximized now, or maximized before it was minimized.</summary>
    public bool Maximized => _window.WindowState == WindowState.Minimized ? _maximized : _window.WindowState is WindowState.Maximized or WindowState.FullScreen;

    private void Note()
    {
        if (_window.WindowState != WindowState.Normal)
        {
            return;
        }

        // A minimized window can report its parking spot before its state changes.
        if (_window.Position.X > -30000 && _window.Position.Y > -30000)
        {
            Position = _window.Position;
        }

        var size = _window.ClientSize;
        if (size.Width > 0 && size.Height > 0)
        {
            Size = size;
        }
        else if (!double.IsNaN(_window.Width) && !double.IsNaN(_window.Height))
        {
            Size = new Size(_window.Width, _window.Height);
        }
    }
}
