using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Hearthpulse.UI.Main.Decks;

public partial class DeckDetailsView : UserControl
{
    public DeckDetailsView() => InitializeComponent();

    /// <summary>Shows the hover card of one chart point (the UI render check uses it; -1 hides it).</summary>
    public void HoverGame(int index) => WinRateChart.HoverIndex = index;

    /// <summary>The Versions card, so the UI render check can crop it.</summary>
    public Control Versions => VersionsCard;

    /// <summary>Opens the ⋯ menu of the last version row (UI render check: not the main one when there are several); null without one.</summary>
    public MenuFlyout? OpenLastVersionMenu()
    {
        var button = VersionsCard.GetVisualDescendants().OfType<Button>().LastOrDefault(b => b.Flyout is MenuFlyout && b.IsEffectivelyVisible);
        if (button?.Flyout is not MenuFlyout menu)
        {
            return null;
        }

        menu.ShowAt(button);
        return menu;
    }

    private DeckDetailsViewModel? Page => DataContext as DeckDetailsViewModel;

    // The ⋯ menu's items: the flyout is not in the page's tree, so they reach the page's commands from here.
    private void OnMakeMain(object? sender, RoutedEventArgs e) => Page?.SetMain.Execute(KeyOf(sender));

    private void OnSplit(object? sender, RoutedEventArgs e) => Page?.Split.Execute(KeyOf(sender));

    private void OnToggleDiff(object? sender, RoutedEventArgs e) => Page?.ToggleDiff.Execute(KeyOf(sender));

    private static string? KeyOf(object? sender) => (sender as Control)?.DataContext is DeckVersionRow row ? row.Key : null;

    /// <summary>The header's pencil: opens the name box in the Versions card and focuses it.</summary>
    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (Page is not { } page)
        {
            return;
        }

        page.StartRename.Execute(null);
        VersionsCard.BringIntoView();
        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        });
    }
}
