using System.Windows.Interop;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// Ctrl+Shift hotkeys on a message-only window of the app's own: never shown, not in Alt+Tab, living as long as the app.
/// They used to sit on the first main window, which a theme change replaces and the tray lets go, and went with it.
/// </summary>
public sealed class HotkeyWindow : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];

    public HotkeyWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("Hearthpulse hotkeys") { ParentWindow = Native.HWND_MESSAGE, WindowStyle = 0 });
        _source.AddHook(OnMessage);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>
    /// Runs <paramref name="action"/> on Ctrl+Shift+<paramref name="key"/> (a virtual-key code).
    /// </summary>
    /// <returns>False when another app holds the combination: the action then never comes.</returns>
    public bool Register(int id, uint key, Action action)
    {
        _actions[id] = action;
        return Native.RegisterHotKey(Handle, id, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, key);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
        {
            Native.UnregisterHotKey(Handle, id);
        }

        _actions.Clear();
        _source.Dispose();
    }
}
