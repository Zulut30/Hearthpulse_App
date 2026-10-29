using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// A dashboard let go in the tray is rebuilt from its normal bounds and "maximized" (WPF's RestoreBounds): not from
    /// the screen-sized bounds of a maximized window, nor the (-32000, -32000) of a minimized one. Win32 reports the new
    /// position and size before the new state (WM_MOVE, WM_SIZE, then the state), so this test changes them in that
    /// order, with no dispatcher pass in between.
    /// </summary>
    [Fact]
    public void A_rebuilt_dashboard_takes_the_normal_bounds_not_the_maximized_or_minimized_ones() => Headless.Run(RestoreBounds);

    private static void RestoreBounds()
    {
        var window = new Window { Width = 1000, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(100, 50) };
        var again = new Window();
        try
        {
            var bounds = new WindowRestoreBounds(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), false), bounds.Current);

            // Maximized by the system: bounds of the screen first, while the state still reads Normal, then the state.
            window.Position = new PixelPoint(-8, -8);
            window.Width = 1936;
            window.Height = 1056;
            window.WindowState = WindowState.Maximized;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), true), bounds.Current);

            // Restored by the system: the normal bounds come while the state still reads Maximized, then the state.
            window.Position = new PixelPoint(100, 50);
            window.Width = 1000;
            window.Height = 700;
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), false), bounds.Current);

            // Maximized, then minimized (closed to the tray from the taskbar menu): parked off screen, still "maximized".
            window.Position = new PixelPoint(-8, -8);
            window.Width = 1936;
            window.Height = 1056;
            window.WindowState = WindowState.Maximized;
            window.Position = new PixelPoint(-32000, -32000);
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), true), bounds.Current);

            // Rebuilt from them: where and as large as the player had it.
            var (position, size, _) = bounds.Current;
            again.WindowStartupLocation = WindowStartupLocation.Manual;
            again.Position = position;
            again.Width = size.Width;
            again.Height = size.Height;
            again.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), 1000d, 700d), (again.Position, again.Width, again.Height));

            // Normal again and moved by the player: that counts.
            window.WindowState = WindowState.Normal;
            window.Position = new PixelPoint(300, 200);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(300, 200), false), (bounds.Current.Position, bounds.Current.Maximized));
        }
        finally
        {
            window.Close();
            again.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// On Windows the normal bounds are the system's placement: the outer rectangle in work-area coordinates, moved by
    /// where the work area starts (a taskbar on the left) and less the frame, in device-independent units for the size.
    /// </summary>
    [Fact]
    public void The_system_placement_turns_into_screen_position_and_client_size()
    {
        var (position, size) = WindowRestoreBounds.NormalBounds(
            new PixelRect(100, 50, 1016, 739), new PixelPoint(48, 0), new Thickness(8, 31, 8, 8), 1.25);
        Assert.Equal(new PixelPoint(148, 50), position);
        Assert.Equal(new Size(800, 560), size);

        // The taskbar at the bottom: work-area and screen coordinates are the same.
        Assert.Equal(new PixelPoint(100, 50), WindowRestoreBounds.NormalBounds(new PixelRect(100, 50, 1016, 739), default, default, 1).Position);
    }
}
