using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Meta;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    [Fact]
    public void Avalonia_meta_views_load_and_the_chosen_archetype_row_takes_the_selected_plate() => Headless.Run(MetaViews);

    private static void MetaViews()
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        try
        {
            // Both views resolve their own resources and styles (Main/Meta/MetaResources, MetaStyles) when they load.
            var constructed = new ConstructedMetaView();
            var battlegrounds = new BattlegroundsTiersView();
            window.Content = new StackPanel { Children = { constructed, battlegrounds } };
            Dispatcher.UIThread.RunJobs();

            constructed.ShowTab("Matchups");
            Assert.True(constructed.IsVisible);
            constructed.ShowTab(null);
            Assert.False(constructed.IsVisible);
            battlegrounds.ShowTab("Trinkets");
            Assert.Equal(["TrinketTierCard"], battlegrounds.GetLogicalDescendants().OfType<Border>().Where(b => b.IsVisible && b.Name is { Length: > 0 } n && n.EndsWith("Card", StringComparison.Ordinal)).Select(b => b.Name));

            // The row templates are picked by the row's type (a tier's head or an archetype); the archetype's button is
            // its own plate and takes the "selected" class.
            var row = new MetaArchetypeRow(1, 1, "Зоолок", "Zoolock", "HERO_07", "Zoolock · Warlock · 5,000 games", "S", "56.0%", "", 0.06, 1, 0.6, "20.0%", 1, "5,000", "—", "—");
            var template = Assert.Single(constructed.DataTemplates, t => t.Match(row));
            var button = Assert.IsType<Button>(template.Build(row));
            button.DataContext = row;
            Assert.Contains("metaRow", button.Classes);
            Assert.DoesNotContain("selected", button.Classes);
            row.IsSelected = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", button.Classes);

            var head = new TierHeadRow("A", "Tier A", "from 51% · 3 archetypes", false);
            var plate = Assert.IsType<Border>(Assert.Single(constructed.DataTemplates, t => t.Match(head)).Build(head));
            Assert.Contains("tierRow", plate.Classes);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
