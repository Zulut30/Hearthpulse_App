using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// Mouse and keyboard state for the card pool panels, read without taking input from the game: the compact panel lets
/// clicks through to the board, and the overlay never has keyboard focus. Windows only (callers check).
/// </summary>
internal static class PoolInput
{
    public const int RightButton = 0x02;

    public const int Escape = 0x1B;

    private static IntPtr _checkedWindow;
    private static bool _checkedIsGame;

    public static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>The cursor in screen pixels.</summary>
    public static (int X, int Y)? Cursor() => GetCursorPos(out var p) ? (p.X, p.Y) : null;

    /// <summary>Hearthstone or this app is the foreground window, so a key press is meant for them.</summary>
    public static bool GameOrAppInFront()
    {
        var window = GetForegroundWindow();
        if (window == _checkedWindow)
        {
            return _checkedIsGame;
        }

        _checkedWindow = window;
        _checkedIsGame = false;
        if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                _checkedIsGame = pid == Environment.ProcessId || process.ProcessName.Equals("Hearthstone", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
            }
        }

        return _checkedIsGame;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>Windows animation setting ("Animate controls and elements inside windows"); false elsewhere.</summary>
    public static bool AnimationsOn() =>
        OperatingSystem.IsWindows() && SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var on, 0) && on;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);
}
