using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Mouse and keyboard state for the card pool panels, read without taking input from the game: the compact panel lets
/// clicks through to the board, and the overlay never has keyboard focus.
/// </summary>
internal static partial class PoolInput
{
    public const int LeftButton = 0x01;

    public const int RightButton = 0x02;

    public const int Escape = 0x1B;

    private static IntPtr _checkedWindow;
    private static bool _checkedIsGame;

    public static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// Down now, or pressed and released since the last read (a click shorter than the poll interval). The "since" bit is
    /// best effort: another reader of the same key clears it.
    /// </summary>
    public static bool WasPressed(int key) => (GetAsyncKeyState(key) & 0x8001) != 0;

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

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
