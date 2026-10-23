using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Desktop;

/// <summary>
/// The window an unexpected error shows: what happened, that it is in the log, and "Open log" / "Continue" (or
/// "Close" when the app has to stop). Built in code so it still works when a resource is what failed.
/// </summary>
internal sealed class CrashWindow : Window
{
    private static bool _open;

    private CrashWindow(Exception ex, bool fatal)
    {
        Title = "Hearthpulse";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        if (Application.Current?.TryFindResource("Surface", out var surface) == true && surface is IBrush background)
        {
            Background = background;
        }

        var muted = Application.Current?.TryFindResource("Muted", out var m) == true && m is IBrush brush ? brush : Brushes.Gray;
        var openLog = new Button { Content = Loc.T("Host.Crash.OpenLog"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6) };
        openLog.Classes.Add("ghost");
        var close = new Button { Content = fatal ? Loc.T("Host.Crash.Close") : Loc.T("Host.Crash.Continue"), IsDefault = true, IsCancel = true, Padding = new Thickness(14, 6) };
        close.Classes.Add("ghost");
        close.Classes.Add("primary");
        openLog.Click += (_, _) => OpenLog();
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children =
            {
                new TextBlock { Text = fatal ? Loc.T("Host.Crash.FatalTitle") : Loc.T("Host.Crash.Title"), FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) },
                new TextBlock
                {
                    Text = fatal
                        ? Loc.T("Host.Crash.FatalText")
                        : Loc.T("Host.Crash.Text"),
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = CrashLog.Describe(ex), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = muted, Margin = new Thickness(0, 8, 0, 16) },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { openLog, close } },
            },
        };
    }

    /// <summary>Shows the window on the UI thread; one at a time, an error raised meanwhile is only logged.</summary>
    public static Task ShowAsync(Exception ex, bool fatal)
    {
        if (_open)
        {
            return Task.CompletedTask;
        }

        _open = true;
        try
        {
            var window = new CrashWindow(ex, fatal);
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) =>
            {
                _open = false;
                closed.TrySetResult();
            };
            window.Show();
            return closed.Task;
        }
        catch (Exception dialogError)
        {
            _open = false;
            Diag.Error("Error window could not be shown", dialogError);
            return Task.CompletedTask;
        }
    }

    private static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Diag.Error("The log could not be opened", ex);
        }
    }
}
