using System.Windows;
using System.Windows.Media;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Every preset builds into a WPF dictionary: the recoloured XAML parses, the backdrop draws the preset's own motif and
/// every brush, geometry and drawing comes out frozen.
/// </summary>
public sealed class ThemeEngineTests
{
    // ThemeEngine keeps a pack:// URI of the built-in theme; without an Application the scheme needs registering.
    static ThemeEngineTests() => _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

    public static TheoryData<string> Presets() => new(ThemePalette.Presets.Select(p => p.Name).Append("Custom"));

    [Theory]
    [MemberData(nameof(Presets))]
    public void Preset_builds_frozen_with_its_own_motif(string name)
    {
        var theme = ThemePalette.Resolve(name, 330, 300, 0.9);
        OnSta(() =>
        {
            var dictionary = ThemeEngine.Build(theme);
            ThemeEngine.Freeze(dictionary);

            var motif = (Geometry)dictionary["Motif" + theme.Motif];
            var badge = (DrawingGroup)((DrawingBrush)dictionary["MotifBrush"]).Drawing;
            Assert.Same(motif, ((GeometryDrawing)badge.Children[0]).Geometry);
            var backdrop = (DrawingGroup)((DrawingBrush)dictionary["ContentFill"]).Drawing;
            var carved = (DrawingGroup)backdrop.Children[^1];
            Assert.Same(motif, ((GeometryDrawing)carved.Children[0]).Geometry);
            Assert.Same(dictionary["Motif" + theme.Motif + "Alt"], ((GeometryDrawing)carved.Children[1]).Geometry);

            var unfrozen = dictionary.Keys.Cast<object>().Where(k => dictionary[k] is Freezable { IsFrozen: false }).ToList();
            Assert.Empty(unfrozen);
        });
    }

    [Fact]
    public void Second_accent_of_a_preset_is_recoloured_in_the_dictionary()
    {
        OnSta(() =>
        {
            var tavern = ((SolidColorBrush)ThemeEngine.Build(ThemePalette.Presets[0])["Accent2"]).Color;
            var frost = ((SolidColorBrush)ThemeEngine.Build(ThemePalette.Resolve("Frost", 0, 0, 1))["Accent2"]).Color;
            Assert.Equal(Color.FromRgb(0x4F, 0xC2, 0xA8), tavern);
            Assert.True(frost.B > frost.G, $"expected an aurora violet, got {frost}");
        });
    }

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }
}
