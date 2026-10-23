using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Hearthlens.Desktop.Platform;

/// <summary>
/// macOS: finds Hearthstone's window in the on-screen window list (no special permission needed for owner and
/// bounds) and makes the overlay a status-level panel that ignores the mouse and follows the player across Spaces.
/// Coordinates are in points, which is what Avalonia uses for window positions on macOS.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOverlayPlatform : IOverlayPlatform
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private const uint OnScreenOnly = 1 << 0;
    private const uint ExcludeDesktopElements = 1 << 4;
    private const int CFNumberIntType = 9;
    private const nint StatusWindowLevel = 25;
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint Stationary = 1 << 4;
    private const nuint IgnoresCycle = 1 << 6;
    private const nuint FullScreenAuxiliary = 1 << 8;
    private const double TitleBarHeight = 28;

    private readonly IntPtr _ownerName;
    private readonly IntPtr _layer;
    private readonly IntPtr _bounds;
    private IntPtr _window;
    private bool? _clickThrough;
    private bool _gameInFront;

    public MacOverlayPlatform()
    {
        var library = NativeLibrary.Load(CoreGraphics);
        _ownerName = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCGWindowOwnerName"));
        _layer = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCGWindowLayer"));
        _bounds = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCGWindowBounds"));
    }

    public bool Supported => true;

    public void Attach(Window overlay)
    {
        _window = (overlay.TryGetPlatformHandle() as IMacOSTopLevelPlatformHandle)?.NSWindow ?? IntPtr.Zero;
        if (_window == IntPtr.Zero)
        {
            return;
        }

        Send(_window, "setLevel:", StatusWindowLevel);
        Send(_window, "setCollectionBehavior:", CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
        Send(_window, "setHidesOnDeactivate:", false);
        SetClickThrough(true);
    }

    public PixelRect? FindGameClientArea()
    {
        var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
        if (list == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            PixelRect? found = null;
            var frontmostSeen = false;
            _gameInFront = false;
            var count = CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var info = CFArrayGetValueAtIndex(list, i);
                if (Layer(info) != 0)
                {
                    continue;
                }

                // The list is ordered front to back: the first normal window belongs to the active app.
                var isGame = Text(CFDictionaryGetValue(info, _ownerName)) == "Hearthstone";
                if (!frontmostSeen)
                {
                    frontmostSeen = true;
                    _gameInFront = isGame;
                }

                if (isGame && found is null && CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(info, _bounds), out var r)
                    && r.Width >= 200 && r.Height >= 200)
                {
                    // Windowed mode includes the title bar in the bounds; full screen fills a display exactly.
                    var titleBar = FillsADisplay(r) ? 0 : TitleBarHeight;
                    found = new PixelRect((int)r.X, (int)(r.Y + titleBar), (int)r.Width, (int)(r.Height - titleBar));
                }
            }

            return found;
        }
        finally
        {
            CFRelease(list);
        }
    }

    public bool IsGameForeground() => _gameInFront;

    public PixelPoint? PointerPosition()
    {
        var e = CGEventCreate(IntPtr.Zero);
        if (e == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var p = CGEventGetLocation(e);
            return new PixelPoint((int)p.X, (int)p.Y);
        }
        finally
        {
            CFRelease(e);
        }
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (_window == IntPtr.Zero || clickThrough == _clickThrough)
        {
            return;
        }

        _clickThrough = clickThrough;
        Send(_window, "setIgnoresMouseEvents:", clickThrough);
    }

    public void Dispose()
    {
    }

    private int Layer(IntPtr info)
    {
        var number = CFDictionaryGetValue(info, _layer);
        return number != IntPtr.Zero && CFNumberGetValue(number, CFNumberIntType, out var layer) ? layer : -1;
    }

    private static bool FillsADisplay(CGRect r)
    {
        var displays = new uint[16];
        if (CGGetActiveDisplayList((uint)displays.Length, displays, out var count) != 0)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var d = CGDisplayBounds(displays[i]);
            if (Math.Abs(d.X - r.X) < 1 && Math.Abs(d.Y - r.Y) < 1 && Math.Abs(d.Width - r.Width) < 1 && Math.Abs(d.Height - r.Height) < 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Text(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
        {
            return null;
        }

        var length = CFStringGetLength(cfString);
        var buffer = new char[length];
        CFStringGetCharacters(cfString, new CFRange { Location = 0, Length = length }, buffer);
        return new string(buffer);
    }

    private static void Send(IntPtr receiver, string selector, bool value) => SendBool(receiver, sel_registerName(selector), value);

    private static void Send(IntPtr receiver, string selector, nint value) => SendInteger(receiver, sel_registerName(selector), value);

    private static void Send(IntPtr receiver, string selector, nuint value) => SendUnsigned(receiver, sel_registerName(selector), value);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location, Length;
    }

    [DllImport(CoreGraphics)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out CGRect rect);

    [DllImport(CoreGraphics)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CoreGraphics)] private static extern CGPoint CGEventGetLocation(IntPtr e);
    [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint max, [Out] uint[] displays, out uint count);
    [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);

    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr s);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(IntPtr s, CFRange range, [Out] char[] buffer);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendInteger(IntPtr receiver, IntPtr selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendUnsigned(IntPtr receiver, IntPtr selector, nuint value);
}
