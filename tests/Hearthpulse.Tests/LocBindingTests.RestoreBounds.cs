using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// A dashboard let go in the tray is rebuilt from its normal bounds and "maximized" (WPF's RestoreBounds): not from
    /// the screen-sized bounds of a maximized window, nor the (-32000, -32000) of a minimized one, which would rebuild
    /// it full-screen or off screen.
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
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), false), (bounds.Position, bounds.Size, bounds.Maximized));

            // Maximized: the platform makes it the size of the screen.
            window.WindowState = WindowState.Maximized;
            window.Position = new PixelPoint(0, 0);
            window.Width = 1920;
            window.Height = 1040;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), true), (bounds.Position, bounds.Size, bounds.Maximized));

            // Minimized from there (closed to the tray from the taskbar menu): parked off screen, still "maximized".
            window.WindowState = WindowState.Minimized;
            window.Position = new PixelPoint(-32000, -32000);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), new Size(1000, 700), true), (bounds.Position, bounds.Size, bounds.Maximized));

            // Rebuilt from them and restored: where and as large as the player had it.
            again.WindowStartupLocation = WindowStartupLocation.Manual;
            again.Position = bounds.Position;
            again.Width = bounds.Size.Width;
            again.Height = bounds.Size.Height;
            again.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((new PixelPoint(100, 50), 1000d, 700d), (again.Position, again.Width, again.Height));

            // Back in the normal state, a move counts again.
            window.WindowState = WindowState.Normal;
            window.Position = new PixelPoint(300, 200);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new PixelPoint(300, 200), bounds.Position);
            Assert.False(bounds.Maximized);
        }
        finally
        {
            window.Close();
            again.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
