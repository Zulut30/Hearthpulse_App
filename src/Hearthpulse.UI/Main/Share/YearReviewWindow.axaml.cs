using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.Share;

/// <summary>The year in review page (proposal A3): the year's tiles and "Share as a picture".</summary>
public partial class YearReviewWindow : Window
{
    private readonly ShareViewModel _model = null!;

    /// <summary>For the XAML loader only.</summary>
    public YearReviewWindow() => InitializeComponent();

    public YearReviewWindow(ShareViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <summary>Opens the page over <paramref name="owner"/>'s window (not modal).</summary>
    public static YearReviewWindow Open(Control? owner, ShareViewModel model)
    {
        var window = new YearReviewWindow(model);
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

    private void OnShare(object? sender, RoutedEventArgs e) => ShareWindow.Open(this, _model);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
