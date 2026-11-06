using System.Windows;

namespace Hearthpulse.App.Main.Share;

/// <summary>The year in review page (proposal A3): the year's tiles and "Share as a picture".</summary>
public partial class YearReviewWindow : Window
{
    private readonly ShareViewModel _model;

    public YearReviewWindow(ShareViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        InitializeComponent();
        _model = model;
        DataContext = model;
    }

    /// <summary>Opens the page over <paramref name="owner"/> (not modal).</summary>
    public static YearReviewWindow Open(Window? owner, ShareViewModel model)
    {
        var window = new YearReviewWindow(model) { Owner = owner };
        window.Show();
        return window;
    }

    private void OnShare(object sender, RoutedEventArgs e) => ShareWindow.Open(this, _model);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
