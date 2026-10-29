using Avalonia;
using Avalonia.Controls;
using Hearthpulse.Core;

namespace Hearthpulse.Desktop.Platform;

/// <summary>
/// What the overlay needs from the operating system: where Hearthstone's client area is, whether the game is in
/// front, where the pointer is, and a way to let clicks pass through the overlay to the game. Rectangles and points
/// are in screen pixels, the same space as <see cref="Window.Position"/>.
/// </summary>
internal interface IOverlayPlatform : IDisposable
{
    /// <summary>False when this desktop cannot host a click-through overlay; the overlay then stays hidden.</summary>
    bool Supported { get; }

    /// <summary>Called once the overlay window exists (window styles, levels).</summary>
    void Attach(Window overlay);

    /// <summary>Hearthstone's client area, or null when it is not running, minimised or not found.</summary>
    PixelRect? FindGameClientArea();

    /// <summary>The game (or the overlay itself) has the keyboard focus.</summary>
    bool IsGameForeground();

    PixelPoint? PointerPosition();

    /// <summary>Whether the left and right mouse buttons are down, read globally (the overlay may let clicks through).</summary>
    (bool Left, bool Right) MouseButtons();

    /// <summary>Whether Ctrl is held, read globally (the game has the keyboard): over a panel it lets the panel be dragged.</summary>
    bool ControlDown();

    /// <summary>True lets every click through to the game; false makes the overlay take the mouse.</summary>
    void SetClickThrough(bool clickThrough);
}

internal static class OverlayPlatform
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The X11 platform is returned to the caller, which owns it for the overlay's lifetime.")]
    public static IOverlayPlatform Create()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsOverlayPlatform();
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacOverlayPlatform();
            }

            if (OperatingSystem.IsLinux() && X11OverlayPlatform.TryCreate() is { } x11)
            {
                return x11;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Diag.Error("Overlay support unavailable", ex);
        }

        return new NoOverlayPlatform();
    }
}

/// <summary>A desktop without a usable overlay (e.g. a pure Wayland session): the dashboard still works.</summary>
internal sealed class NoOverlayPlatform : IOverlayPlatform
{
    public bool Supported => false;

    public void Attach(Window overlay)
    {
    }

    public PixelRect? FindGameClientArea() => null;

    public bool IsGameForeground() => false;

    public PixelPoint? PointerPosition() => null;

    public (bool Left, bool Right) MouseButtons() => (false, false);

    public bool ControlDown() => false;

    public void SetClickThrough(bool clickThrough)
    {
    }

    public void Dispose()
    {
    }
}
