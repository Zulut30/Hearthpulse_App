using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Hearthlens.Desktop.Platform;

/// <summary>
/// Linux (X11 and XWayland): finds Hearthstone's Wine window through the window manager's client list and makes the
/// overlay click-through with an empty XShape input region.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11OverlayPlatform : IOverlayPlatform
{
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private const int Unsorted = 0;
    private const int IsViewable = 2;

    private readonly IntPtr _display;
    private readonly nuint _root;
    private readonly nuint _clientList;
    private readonly nuint _activeWindow;
    private nuint _overlay;
    private nuint _game;
    private DateTime _nextLookup;
    private bool? _clickThrough;

    private X11OverlayPlatform(IntPtr display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
        _clientList = XInternAtom(display, "_NET_CLIENT_LIST", false);
        _activeWindow = XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
    }

    public bool Supported => true;

    public static X11OverlayPlatform? TryCreate()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return null;
        }

        var display = XOpenDisplay(IntPtr.Zero);
        return display == IntPtr.Zero ? null : new X11OverlayPlatform(display);
    }

    public void Attach(Window overlay)
    {
        _overlay = (nuint)(overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        SetClickThrough(true);
    }

    public PixelRect? FindGameClientArea()
    {
        var now = DateTime.UtcNow;
        if (_game == 0 || now >= _nextLookup)
        {
            _nextLookup = now.AddSeconds(1.5);
            _game = FindHearthstoneWindow();
        }

        if (_game == 0 || XGetWindowAttributes(_display, _game, out var attributes) == 0 || attributes.map_state != IsViewable
            || attributes.width < 200 || attributes.height < 200)
        {
            return null;
        }

        if (XTranslateCoordinates(_display, _game, _root, 0, 0, out var x, out var y, out _) == 0)
        {
            return null;
        }

        return new PixelRect(x, y, attributes.width, attributes.height);
    }

    public bool IsGameForeground()
    {
        var active = WindowProperty(_root, _activeWindow).FirstOrDefault();
        return active != 0 && (active == _game || active == _overlay);
    }

    public PixelPoint? PointerPosition() =>
        XQueryPointer(_display, _root, out _, out _, out var x, out var y, out _, out _, out _) != 0 ? new PixelPoint(x, y) : null;

    public void SetClickThrough(bool clickThrough)
    {
        if (_overlay == 0 || clickThrough == _clickThrough)
        {
            return;
        }

        _clickThrough = clickThrough;
        if (clickThrough)
        {
            // An empty input region: every click goes to the window below.
            XShapeCombineRectangles(_display, _overlay, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
        }
        else
        {
            // No mask: the whole window takes input again.
            XShapeCombineMask(_display, _overlay, ShapeInput, 0, 0, 0, ShapeSet);
        }

        XFlush(_display);
    }

    public void Dispose() => XCloseDisplay(_display);

    private nuint FindHearthstoneWindow()
    {
        foreach (var window in WindowProperty(_root, _clientList))
        {
            var hint = default(XClassHint);
            if (XGetClassHint(_display, window, ref hint) == 0)
            {
                continue;
            }

            var name = Marshal.PtrToStringUTF8(hint.res_name) ?? string.Empty;
            var cls = Marshal.PtrToStringUTF8(hint.res_class) ?? string.Empty;
            if (hint.res_name != IntPtr.Zero) XFree(hint.res_name);
            if (hint.res_class != IntPtr.Zero) XFree(hint.res_class);

            // Wine names the class after the executable: "hearthstone.exe".
            if (name.Contains("hearthstone", StringComparison.OrdinalIgnoreCase) || cls.Contains("hearthstone", StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return 0;
    }

    /// <summary>Reads a WINDOW-list property (32-bit format items are stored as C longs).</summary>
    private List<nuint> WindowProperty(nuint window, nuint property)
    {
        var result = new List<nuint>();
        if (XGetWindowProperty(_display, window, property, 0, 4096, false, 0, out _, out var format, out var count, out _, out var data) != 0
            || data == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            if (format == 32)
            {
                for (var i = 0; i < (int)count; i++)
                {
                    result.Add((nuint)Marshal.ReadIntPtr(data, i * IntPtr.Size));
                }
            }
        }
        finally
        {
            XFree(data);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClassHint
    {
        public IntPtr res_name;
        public IntPtr res_class;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual;
        public nuint root;
        public int c_class, bit_gravity, win_gravity, backing_store;
        public nuint backing_planes, backing_pixel;
        public int save_under;
        public nuint colormap;
        public int map_installed, map_state;
        public nint all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }

    private const string X11 = "libX11.so.6";
    private const string Xext = "libXext.so.6";

    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] private static extern nuint XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] private static extern nuint XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(X11)] private static extern int XFree(IntPtr data);
    [DllImport(X11)] private static extern int XFlush(IntPtr display);
    [DllImport(X11)] private static extern int XGetClassHint(IntPtr display, nuint window, ref XClassHint hint);
    [DllImport(X11)] private static extern int XGetWindowAttributes(IntPtr display, nuint window, out XWindowAttributes attributes);

    [DllImport(X11)]
    private static extern int XTranslateCoordinates(IntPtr display, nuint source, nuint destination, int sourceX, int sourceY,
        out int destinationX, out int destinationY, out nuint child);

    [DllImport(X11)]
    private static extern int XQueryPointer(IntPtr display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY,
        out int windowX, out int windowY, out uint mask);

    [DllImport(X11)]
    private static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, bool delete,
        nuint requestedType, out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out IntPtr data);

    [DllImport(Xext)]
    private static extern void XShapeCombineRectangles(IntPtr display, nuint window, int kind, int x, int y, IntPtr rectangles, int count,
        int operation, int ordering);

    [DllImport(Xext)]
    private static extern void XShapeCombineMask(IntPtr display, nuint window, int kind, int x, int y, nuint mask, int operation);
}
