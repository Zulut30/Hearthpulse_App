using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// A window's normal bounds and whether it is maximized, what WPF keeps as RestoreBounds: a window rebuilt from them
/// opens where the player had it. On Windows they come from the system (<c>GetWindowPlacement</c>, the source of WPF's
/// RestoreBounds). Elsewhere they are noted while the window is in its normal state; a platform may report the bounds of
/// a maximize or minimize before the new state (Win32 does: WM_MOVE, WM_SIZE, then the state), so noted bounds count only
/// if the window is still normal once the dispatcher has moved on.
/// </summary>
public sealed class WindowRestoreBounds
{
    private readonly Window _window;
    private PixelPoint _position;
    private Size _size;
    private bool _maximized;

    public WindowRestoreBounds(Window window)
    {
        _window = window;
        _position = window.Position;
        _size = SizeOf(window);
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

                // Back to normal: the bounds that came just before the state are the normal ones.
                Note();
            }
            else if (e.Property == TopLevel.ClientSizeProperty || e.Property == Layoutable.WidthProperty || e.Property == Layoutable.HeightProperty)
            {
                Note();
            }
        };
    }

    /// <summary>The normal bounds (top-left in screen pixels, client size) and whether the window is (or was, if minimized) maximized.</summary>
    public (PixelPoint Position, Size Size, bool Maximized) Current =>
        (OperatingSystem.IsWindows() ? FromSystem() : null)
        ?? (_position, _size, _window.WindowState == WindowState.Minimized ? _maximized : _window.WindowState is WindowState.Maximized or WindowState.FullScreen);

    /// <summary>
    /// The normal bounds from the system's placement of the window: its outer rectangle in work-area coordinates, moved
    /// to screen coordinates (a taskbar on the left or top shifts them) and less the frame for the client size.
    /// </summary>
    /// <param name="normal">The outer normal rectangle (<c>rcNormalPosition</c>), work-area coordinates, pixels.</param>
    /// <param name="workAreaOffset">Where the monitor's work area starts against the monitor (0, 0 with the taskbar at the bottom).</param>
    /// <param name="frame">The window frame around the client area, pixels.</param>
    /// <param name="scaling">Pixels per device-independent unit.</param>
    public static (PixelPoint Position, Size Size) NormalBounds(PixelRect normal, PixelPoint workAreaOffset, Thickness frame, double scaling) =>
        (new PixelPoint(normal.X + workAreaOffset.X, normal.Y + workAreaOffset.Y),
            new Size((normal.Width - frame.Left - frame.Right) / scaling, (normal.Height - frame.Top - frame.Bottom) / scaling));

    private void Note()
    {
        if (_window.WindowState != WindowState.Normal)
        {
            return;
        }

        var position = _window.Position;
        var size = SizeOf(_window);
        Dispatcher.UIThread.Post(() =>
        {
            if (_window.WindowState != WindowState.Normal)
            {
                return;
            }

            // A minimized window can report its parking spot (-32000, -32000) before its state changes.
            if (position.X > -30000 && position.Y > -30000)
            {
                _position = position;
            }

            if (size.Width > 0 && size.Height > 0)
            {
                _size = size;
            }
        }, DispatcherPriority.Background);
    }

    private static Size SizeOf(Window window)
    {
        var client = window.ClientSize;
        return client.Width > 0 && client.Height > 0 ? client
            : !double.IsNaN(window.Width) && !double.IsNaN(window.Height) ? new Size(window.Width, window.Height)
            : default;
    }

    private (PixelPoint, Size, bool)? FromSystem()
    {
        if (_window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: var hwnd } || hwnd == IntPtr.Zero)
        {
            return null;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var normal = placement.NormalPosition;
        var offset = default(PixelPoint);
        // A tool window's placement is in screen coordinates, any other's in its monitor's work area.
        if ((GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) == 0)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var monitor = MonitorFromRect(ref normal, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                offset = new PixelPoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
            }
        }

        var dpi = GetDpiForWindow(hwnd);
        var insets = default(Rect32);
        var frame = AdjustWindowRectExForDpi(ref insets, GetWindowLong(hwnd, GwlStyle), false, GetWindowLong(hwnd, GwlExStyle), dpi)
            ? new Thickness(-insets.Left, -insets.Top, insets.Right, insets.Bottom)
            : default;
        var (position, size) = NormalBounds(
            new PixelRect(normal.Left, normal.Top, normal.Right - normal.Left, normal.Bottom - normal.Top), offset, frame, dpi > 0 ? dpi / 96.0 : 1);
        var maximized = IsZoomed(hwnd) || placement.ShowCmd == SwShowMaximized
                        || (IsIconic(hwnd) && (placement.Flags & WpfRestoreToMaximized) != 0);
        return (position, size, maximized);
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public Rect32 NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }

    // DllImport, not LibraryImport: this project does not allow unsafe code. Called on Windows only.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref Rect32 rect, int style, [MarshalAs(UnmanagedType.Bool)] bool menu, int exStyle, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
