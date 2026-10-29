using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Hearthpulse.App.Main.Decks;

public partial class DeckDetailsView : UserControl
{
    public DeckDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>The win-rate chart, so the UI render check can show a hover card.</summary>
    public Controls.StatsLineChart Chart => WinRateChart;

    /// <summary>The Versions card, so the UI render check can crop it.</summary>
    public FrameworkElement Versions => VersionsCard;

    /// <summary>Opens the ⋯ menu of the last version row (UI render check: not the main one when there are several); null without one.</summary>
    public ContextMenu? OpenLastVersionMenu()
    {
        if (MoreButtons(VersionsCard).LastOrDefault() is not { ContextMenu: { } menu } button)
        {
            return null;
        }

        OpenMenu(button, menu);
        return menu;
    }

    /// <summary>A version row's ⋯ button: its context menu opens under it on a left click too.</summary>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            OpenMenu(button, menu);
        }
    }

    private static void OpenMenu(Button button, ContextMenu menu)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The header's pencil: opens the name box in the Versions card and focuses it.</summary>
    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DeckDetailsViewModel page)
        {
            return;
        }

        page.StartRename.Execute(null);
        VersionsCard.BringIntoView();
        _ = Dispatcher.BeginInvoke(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }

    private static IEnumerable<Button> MoreButtons(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Button { ContextMenu: not null, IsVisible: true } button)
            {
                yield return button;
            }

            foreach (var found in MoreButtons(child))
            {
                yield return found;
            }
        }
    }
}
