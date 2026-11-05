using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>The Avalonia V3 deck row: the legendary colour, the drawn-out row and the rarity-lit edge.</summary>
public sealed partial class LocBindingTests
{
    /// <summary>
    /// A legendary row draws its name and star in the legendary colour; a drawn-out row reads faint even when it is
    /// legendary (OverlayLook's empty rule comes after the legendary one); the edge takes the rarity's own edge brush.
    /// </summary>
    [Fact]
    public void Deck_row_colours_legendary_drawn_out_and_edge() => Headless.Run(() =>
    {
        var theme = Theme();
        var look = new StyleInclude(new Uri("avares://Hearthpulse.UI/")) { Source = new Uri("avares://Hearthpulse.UI/Overlay/OverlayLook.axaml") };
        Application.Current!.Styles.Add(look);
        var template = Assert.IsAssignableFrom<IDataTemplate>(new DeckPanel().Resources["DeckTileTemplate"]);
        var tiles = new[]
        {
            new DeckTile("EX1_383", "Tirion", 8, "★", true, false, "LEGENDARY", string.Empty, false),
            new DeckTile("EX1_383", "Tirion", 8, "★", true, true, "LEGENDARY", string.Empty, false),
            new DeckTile("CS2_029", "Fireball", 4, "2", true, false, "COMMON", string.Empty, false),
        };
        var rows = new StackPanel();
        foreach (var tile in tiles)
        {
            var row = template.Build(tile)!;
            row.DataContext = tile;
            rows.Children.Add(row);
        }

        var window = new Window { Width = 260, Height = 200, Content = rows };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Color Resource(string key) => ((ISolidColorBrush)window.FindResource(key)!).Color;
            Color Name(int row) => ((ISolidColorBrush)rows.Children[row].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("tileName")).Foreground!).Color;
            Color Edge(int row) => ((ISolidColorBrush)rows.Children[row].GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("rarityEdge")).BorderBrush!).Color;

            Assert.Equal(new[] { true, true, false }, tiles.Select(t => t.Legendary));
            Assert.Equal(Resource("RarityLegendary"), Name(0));
            Assert.Equal(Resource("Faint"), Name(1));
            Assert.NotEqual(Resource("RarityLegendary"), Name(2));
            Assert.Equal(Resource("RarityLegendaryEdge"), Edge(0));
            Assert.Equal(Resource("RarityCommonEdge"), Edge(2));
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(look);
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });
}
