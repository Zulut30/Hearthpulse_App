using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) in the Avalonia views. Styles, templates and bindings only:
/// the window is not shown, so nothing measures text (no fonts here).
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Setup_banner_wraps_in_its_card() => Headless.Run(() => WithMainView((view, model) =>
    {
        model.Apply(LiveSnapshot.Empty with { LogConfigMissing = ["Power", "LoadingScreen", "Decks"] });
        Dispatcher.UIThread.RunJobs();

        var banner = view.FindControl<Border>("SetupBanner")!;
        var text = view.FindControl<TextBlock>("SetupWarningText")!;
        Assert.True(banner.IsVisible);
        // The glyph takes what it needs, the text the rest of the card, and it wraps there (as WPF does).
        var grid = Assert.IsType<Grid>(banner.Child);
        Assert.Equal(2, grid.ColumnDefinitions.Count);
        Assert.True(grid.ColumnDefinitions[0].Width.IsAuto && grid.ColumnDefinitions[1].Width.IsStar);
        Assert.Same(grid, text.Parent);
        Assert.Equal(1, Grid.GetColumn(text));
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
        Assert.Equal(model.SetupWarning, text.Text);
    }));
}
