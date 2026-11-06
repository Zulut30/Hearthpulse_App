using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Hearthpulse.Core;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Share;

/// <summary>YouTube timestamps from History (proposal C2): the text to paste into a video description.</summary>
public partial class TimestampsWindow : Window
{
    private readonly TimestampsViewModel _model = null!;

    /// <summary>For the XAML loader only.</summary>
    public TimestampsWindow() => InitializeComponent();

    public TimestampsWindow(TimestampsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <summary>Opens the window over <paramref name="owner"/>'s window (not modal).</summary>
    public static TimestampsWindow Open(Control? owner, TimestampsViewModel model)
    {
        var window = new TimestampsWindow(model);
        if (TopLevel.GetTopLevel(owner) is Window parent)
        {
            window.Show(parent);
        }
        else
        {
            window.Show();
        }

        return window;
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => CopyAsync().Forget("Timestamps copy");

    private async Task CopyAsync()
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                _model.Copied(false);
                return;
            }

            await clipboard.SetTextAsync(_model.Text);
            _model.Copied(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Diag.Error("Copying the timestamps failed", ex);
            _model.Copied(false);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
