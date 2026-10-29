using System.Runtime.InteropServices;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF hotkeys live on a message-only window of the app's own, so a theme change (a new main window) and the tray
/// (no main window) keep them. Registration itself is not checked: a running Hearthpulse holds the combinations.
/// </summary>
public sealed class HotkeyWindowTests
{
    private const int WM_HOTKEY = 0x0312;

    [Fact]
    public void Hotkey_messages_run_their_action_on_a_hidden_window_that_goes_away_on_dispose()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Hotkey window failed", failure);
        }
    }

    private static void Check()
    {
        var ran = new List<string>();
        var window = new HotkeyWindow();
        var handle = window.Handle;
        // Ids far from the app's own (0x484C-0x484E), so this never takes a combination a running app needs.
        window.Register(0x7A01, 0x48, () => ran.Add("H"));
        window.Register(0x7A02, 0x4C, () => ran.Add("L"));
        window.Register(0x7A03, 0x52, () => ran.Add("R"));

        Assert.True(IsWindow(handle));
        Assert.False(IsWindowVisible(handle));
        // A message-only window has no place among the top-level windows (Alt+Tab, the taskbar).
        Assert.NotEqual(GetDesktopWindow(), GetAncestor(handle, GA_PARENT));

        SendMessage(handle, WM_HOTKEY, 0x7A02, 0);
        SendMessage(handle, WM_HOTKEY, 0x7A01, 0);
        SendMessage(handle, WM_HOTKEY, 0x7A03, 0);
        SendMessage(handle, WM_HOTKEY, 0x7A09, 0);
        Assert.Equal(["L", "H", "R"], ran);

        window.Dispose();
        Assert.False(IsWindow(handle));
    }

    private const uint GA_PARENT = 1;

    // DllImport, not LibraryImport: the test project does not allow unsafe code.
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
