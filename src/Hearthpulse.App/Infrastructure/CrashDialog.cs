using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// The window an unexpected error shows: what happened, that it is in the log, and "Open log" / "Continue" (or
/// "Close" when the app has to stop). Built in code so it still works when a XAML resource is what failed.
/// </summary>
internal static class CrashDialog
{
    private static bool _open;

    /// <summary>
    /// Shows the window. With <paramref name="reporter"/> it offers "Send report" (plan 0.5): sent now, or queued for the
    /// next start when the app has to stop; when <paramref name="autoSent"/> the report already went and the window says so.
    /// </summary>
    public static void Show(Exception ex, bool fatal, Hearthpulse.Core.Diagnostics.CrashReporter? reporter = null, string where = "UI error", bool autoSent = false)
    {
        // One at a time: an error raised while the window is up (a timer, a layout pass) is only logged.
        if (_open)
        {
            return;
        }

        _open = true;
        try
        {
            var window = new Window
            {
                Title = "Hearthpulse",
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ShowInTaskbar = true,
                Background = Brush("Surface", Brushes.White),
                Foreground = Brush("Text", Brushes.Black),
            };
            if (Application.Current?.TryFindResource("UiFont") is FontFamily font)
            {
                window.FontFamily = font;
            }

            // The dark surface gets a dark title bar too; with Windows in light mode it would be a white strip on top.
            if (Application.Current?.TryFindResource("Surface") is not null)
            {
                window.SourceInitialized += (_, _) => Native.UseDarkChrome(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            }

            var text = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
            text.Children.Add(new TextBlock
            {
                Text = fatal ? Loc.T("Host.Crash.FatalTitle") : Loc.T("Host.Crash.Title"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            text.Children.Add(new TextBlock
            {
                Text = fatal
                    ? Loc.T("Host.Crash.FatalText")
                    : Loc.T("Host.Crash.Text"),
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = CrashLog.Describe(ex),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brush("Muted", Brushes.Gray),
                Margin = new Thickness(0, 8, 0, 16),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var reportNote = new TextBlock
            {
                // A crash that ends the app is only queued: it goes at the next start.
                Text = autoSent ? Loc.T(fatal ? "Host.Crash.Queued" : "Host.Crash.AutoSent") : string.Empty,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = Brush("Muted", Brushes.Gray),
                Margin = new Thickness(0, 0, 0, 12),
                Visibility = autoSent ? Visibility.Visible : Visibility.Collapsed,
            };
            text.Children.Add(reportNote);
            if (reporter is not null && !autoSent)
            {
                // Plan 0.5: the player's explicit consent for this one report.
                var send = new Button { Content = Loc.T("Host.Crash.Send"), ToolTip = Loc.T("Host.Crash.SendTip"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
                Style(send, "GhostButton");
                send.Click += (_, _) =>
                {
                    send.IsEnabled = false;
                    SendAsync(reporter, where, ex, fatal, reportNote).Forget("Crash report");
                };
                buttons.Children.Add(send);
            }

            var openLog = new Button { Content = Loc.T("Host.Crash.OpenLog"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6) };
            var close = new Button { Content = fatal ? Loc.T("Host.Crash.Close") : Loc.T("Host.Crash.Continue"), IsDefault = true, IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
            Style(openLog, "GhostButton");
            Style(close, "PrimaryButton");
            openLog.Click += (_, _) => OpenLog();
            close.Click += (_, _) => window.Close();
            buttons.Children.Add(openLog);
            buttons.Children.Add(close);
            text.Children.Add(buttons);
            window.Content = text;
            window.ShowDialog();
        }
        catch (Exception dialogError)
        {
            // The window itself failed (resources gone, shutting down): the error is in the log already.
            Diag.Error("Error window could not be shown", dialogError);
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>Sends the report now, or queues it for the next start when the app has to stop, and says which.</summary>
    private static async Task SendAsync(Hearthpulse.Core.Diagnostics.CrashReporter reporter, string where, Exception ex, bool fatal, TextBlock note)
    {
        var sent = false;
        if (fatal)
        {
            reporter.Queue(where, ex);
        }
        else
        {
            sent = await reporter.SendAsync(where, ex);
        }

        note.Text = Loc.T(sent ? "Host.Crash.Sent" : "Host.Crash.Queued");
        note.Visibility = Visibility.Visible;
    }

    private static void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No app for .log files: show the folder instead.
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppPaths.LogFile}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception fallback) when (fallback is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Diag.Error("The log could not be opened", fallback);
            }
        }
    }

    private static Brush Brush(string key, Brush fallback) => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static void Style(Button button, string key)
    {
        if (Application.Current?.TryFindResource(key) is Style style)
        {
            button.Style = style;
        }
    }
}
