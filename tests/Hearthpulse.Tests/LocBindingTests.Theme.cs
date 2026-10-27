using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The Avalonia theme engine recolours Theme.axaml in place and can switch back (it shares the headless app).</summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Avalonia_theme_engine_recolours_in_place_and_switches_back() => Headless.Run(ThemeSwitch);

    private static void ThemeSwitch()
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            var resources = ((Styles)theme.Loaded).Resources;
            var accent = Assert.IsType<SolidColorBrush>(resources["Accent"]);
            var positive = Assert.IsType<SolidColorBrush>(resources["Positive"]);
            var card = Assert.IsAssignableFrom<GradientBrush>(resources["CardFill"]);
            var motif = (GeometryDrawing)((DrawingGroup)Assert.IsType<DrawingBrush>(resources["MotifBrush"]).Drawing!).Children[0];
            var gold = accent.Color;
            var green = positive.Color;
            var plate = card.GradientStops[0].Color;
            var frost = ThemePalette.Presets.Single(p => p.Name == "Frost");

            Assert.True(ThemeEngine.Apply(app, frost));
            Assert.Equal(Color.FromUInt32(ThemePalette.Map(gold.ToUInt32(), frost)), accent.Color);
            Assert.NotEqual(gold, accent.Color);
            Assert.Equal(green, positive.Color);
            Assert.Equal(Color.FromUInt32(ThemePalette.Map(plate.ToUInt32(), frost)), card.GradientStops[0].Color);
            Assert.Equal(Color.FromUInt32(ThemePalette.Map(gold.ToUInt32(), frost)), (Color)resources["AccentColor"]!);
            Assert.Same(resources["Motif" + frost.Motif], motif.Geometry);

            // Applied again or switched back, colours come from the Tavern originals, not from the previous theme.
            Assert.True(ThemeEngine.Apply(app, frost));
            Assert.Equal(Color.FromUInt32(ThemePalette.Map(gold.ToUInt32(), frost)), accent.Color);
            Assert.True(ThemeEngine.Apply(app, ThemePalette.Presets[0]));
            Assert.Equal(gold, accent.Color);
            Assert.Equal(plate, card.GradientStops[0].Color);
            Assert.Same(resources["MotifTavern"], motif.Geometry);

            // The picker tile is drawn in the theme's colours and keeps the preset's key for the command.
            var tile = ThemeChoice.From(frost, selected: true);
            Assert.Equal("Frost", tile.Key);
            Assert.Equal(Color.FromUInt32(ThemePalette.Map(0xFFE9B949, frost)), ((ISolidColorBrush)tile.Accent).Color);
            Assert.NotNull(tile.Motif);
        }
        finally
        {
            ThemeEngine.Apply(app, ThemePalette.Presets[0]);
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
