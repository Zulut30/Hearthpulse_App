using Avalonia;
using Avalonia.Controls;
using Hearthlens.Core;

namespace Hearthlens.Desktop.Platform;

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

    /// <summary>True lets every click through to the game; false makes the overlay take the mouse.</summary>
    void SetClickThrough(bool clickThrough);
}

internal static class OverlayPlatform
{
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

    public void SetClickThrough(bool clickThrough)
    {
    }

    public void Dispose()
    {
    }
}
