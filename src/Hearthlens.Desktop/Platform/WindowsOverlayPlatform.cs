using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Hearthlens.Desktop.Platform;

/// <summary>Win32: finds Hearthstone's window and makes the overlay a layered, click-through tool window.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsOverlayPlatform : IOverlayPlatform
{
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x20;
    private const uint WS_EX_TOOLWINDOW = 0x80;
    private const uint WS_EX_LAYERED = 0x80000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;

    private IntPtr _overlay;
    private IntPtr _game;
    private DateTime _nextLookup;
    private bool _clickThrough = true;

    public bool Supported => true;

    public IntPtr Handle => _overlay;

    public void Attach(Window overlay)
    {
        // Keep our extended styles whenever Avalonia recomputes the window's styles.
        Win32Properties.AddWindowStylesCallback(overlay, (style, exStyle) =>
            (style, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | (_clickThrough ? WS_EX_TRANSPARENT : 0)));
        _overlay = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_overlay != IntPtr.Zero)
        {
            SetExStyle(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT, 0);
            // A layered window stays invisible until its attributes are set; 255 = fully opaque content.
            SetLayeredWindowAttributes(_overlay, 0, 255, LWA_ALPHA);
        }
    }

    public PixelRect? FindGameClientArea()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextLookup || !IsWindow(_game))
        {
            _nextLookup = now.AddSeconds(1.5);
            _game = FindHearthstoneWindow();
        }

        if (_game == IntPtr.Zero || !IsWindow(_game) || IsIconic(_game) || !GetClientRect(_game, out var client))
        {
            return null;
        }

        var origin = new POINT();
        if (!ClientToScreen(_game, ref origin) || client.Right < 200 || client.Bottom < 200)
        {
            return null;
        }

        return new PixelRect(origin.X, origin.Y, client.Right, client.Bottom);
    }

    public bool IsGameForeground()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && (foreground == _game || foreground == _overlay);
    }

    public PixelPoint? PointerPosition() => GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

    public void SetClickThrough(bool clickThrough)
    {
        if (clickThrough == _clickThrough || _overlay == IntPtr.Zero)
        {
            return;
        }

        _clickThrough = clickThrough;
        if (clickThrough)
        {
            SetExStyle(WS_EX_TRANSPARENT, 0);
        }
        else
        {
            SetExStyle(0, WS_EX_TRANSPARENT);
        }
    }

    public void Dispose()
    {
    }

    private void SetExStyle(uint add, uint remove)
    {
        var style = (uint)(long)GetWindowLongPtr(_overlay, GWL_EXSTYLE);
        var updated = (style | add) & ~remove;
        if (updated != style)
        {
            SetWindowLongPtr(_overlay, GWL_EXSTYLE, (IntPtr)updated);
        }
    }

    private static IntPtr FindHearthstoneWindow()
    {
        foreach (var process in Process.GetProcessesByName("Hearthstone"))
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);
}

/// <summary>Global Ctrl+Shift+H (hide overlay) and Ctrl+Shift+L (layout mode) on Windows.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHotkeys : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HideId = 0x484C;
    private const int LayoutId = 0x484D;

    private readonly IntPtr _hwnd;

    public WindowsHotkeys(Window window, Action toggleOverlay, Action toggleLayout)
    {
        _hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32Properties.AddWndProcHookCallback(window, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HideId:
                        toggleOverlay();
                        handled = true;
                        break;
                    case LayoutId:
                        toggleLayout();
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
        });

        if (!RegisterHotKey(_hwnd, HideId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x48))
        {
            Core.Diag.Info("Ctrl+Shift+H is taken by another app; overlay hotkey disabled.");
        }

        if (!RegisterHotKey(_hwnd, LayoutId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4C))
        {
            Core.Diag.Info("Ctrl+Shift+L is taken by another app; layout hotkey disabled.");
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HideId);
            UnregisterHotKey(_hwnd, LayoutId);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
