using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core;

namespace Hearthpulse.App.Main.Share;

/// <summary>Home's "your year is ready" banner (proposal A3): opens the year page (its data: <see cref="MainViewModel"/>).</summary>
public partial class YearReviewBanner : UserControl
{
    public YearReviewBanner() => InitializeComponent();

    private void OnOpen(object sender, RoutedEventArgs e) => OpenAsync().Forget("Year in review");

    private async Task OpenAsync()
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        OpenYearButton.IsEnabled = false;
        try
        {
            if (await model.YearShareAsync() is { } year)
            {
                YearReviewWindow.Open(Window.GetWindow(this), year);
            }
        }
        finally
        {
            OpenYearButton.IsEnabled = true;
        }
    }
}
