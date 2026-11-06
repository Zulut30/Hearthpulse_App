using System.Windows;
using Hearthpulse.Core;

namespace Hearthpulse.App.Main.Share;

/// <summary>YouTube timestamps from History (proposal C2): the text to paste into a video description.</summary>
public partial class TimestampsWindow : Window
{
    private readonly TimestampsViewModel _model;

    public TimestampsWindow(TimestampsViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <summary>Opens the window over <paramref name="owner"/> (not modal).</summary>
    public static TimestampsWindow Open(Window? owner, TimestampsViewModel model)
    {
        var window = new TimestampsWindow(model) { Owner = owner };
        window.Show();
        return window;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_model.Text);
            _model.Copied(true);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Diag.Error("Copying the timestamps failed", ex);
            _model.Copied(false);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
