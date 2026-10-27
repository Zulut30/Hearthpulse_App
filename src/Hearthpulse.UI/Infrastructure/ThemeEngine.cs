using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// Recolours Themes/Theme.axaml for a colour theme, in place. The theme's brushes are mutable objects shared by every
/// control that uses them, so each colour is rewritten through <see cref="ThemePalette.Map"/> from its Tavern original
/// (kept on first use, so themes can be switched back and forth), the backdrop draws the theme's motif and open
/// windows repaint. The WPF twin (Hearthpulse.App ThemeEngine) rebuilds the dictionary from text instead.
/// </summary>
public static class ThemeEngine
{
    private const string Source = "Themes/Theme.axaml";

    /// <summary>Colours that carry meaning never change (same prefixes as the WPF engine).</summary>
    private static readonly string[] FixedKeys = ["Positive", "Negative", "Info", "Rarity", "Tier", "ManaGem", "PoolStat"];

    private static readonly ConditionalWeakTable<object, StrongBox<Color>> Originals = new();
    private static readonly ConditionalWeakTable<GeometryDrawing, Geometry> OriginalGeometry = new();
    private static readonly ConditionalWeakTable<IResourceDictionary, Dictionary<object, Color>> OriginalColors = new();

    /// <summary>Repaints the running app in <paramref name="theme"/>; false when the theme styles are not loaded.</summary>
    public static bool Apply(Application app, ThemeSpec theme)
    {
        if (Find(app.Styles) is not { } resources)
        {
            return false;
        }

        Apply(resources, theme);
        if (app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                Repaint(window);
            }
        }

        return true;
    }

    /// <summary>Rewrites every colour of the theme dictionary for <paramref name="theme"/>.</summary>
    public static void Apply(IResourceDictionary resources, ThemeSpec theme)
    {
        var colors = OriginalColors.GetValue(resources, _ => []);
        Geometry? motif = null, motifAlt = null, tavern = null, tavernAlt = null;
        if (resources.TryGetValue("Motif" + ThemePalette.DefaultName, out var t) && t is Geometry g1)
        {
            tavern = g1;
        }

        if (resources.TryGetValue("Motif" + ThemePalette.DefaultName + "Alt", out var ta) && ta is Geometry g2)
        {
            tavernAlt = g2;
        }

        if (resources.TryGetValue("Motif" + theme.Motif, out var m) && m is Geometry g3)
        {
            motif = g3;
        }

        if (resources.TryGetValue("Motif" + theme.Motif + "Alt", out var ma) && ma is Geometry g4)
        {
            motifAlt = g4;
        }

        foreach (var key in resources.Keys.ToList())
        {
            if (key is string name && FixedKeys.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var value = resources[key];
            if (value is Color color)
            {
                if (!colors.TryGetValue(key, out var original))
                {
                    colors[key] = original = color;
                }

                var mapped = Map(original, theme);
                if (mapped != color)
                {
                    resources[key] = mapped;
                }

                continue;
            }

            Recolor(value, theme, tavern is null ? null : (tavern, motif ?? tavern), tavernAlt is null ? null : (tavernAlt, motifAlt ?? tavernAlt));
        }
    }

    /// <summary>One theme colour as a brush, for previews.</summary>
    public static IBrush Brush(uint argb, ThemeSpec theme) => new ImmutableSolidColorBrush(ToColor(ThemePalette.Map(argb, theme)));

    /// <summary>A theme's backdrop line art (<c>Motif&lt;Name&gt;</c>), or null outside the app.</summary>
    public static Geometry? Motif(string name) =>
        Application.Current is { } app && app.TryGetResource("Motif" + name, app.ActualThemeVariant, out var value) ? value as Geometry : null;

    private static IResourceDictionary? Find(IEnumerable<IStyle> styles)
    {
        foreach (var style in styles)
        {
            if (style is StyleInclude include && include.Source?.ToString().EndsWith(Source, StringComparison.Ordinal) == true)
            {
                return (include.Loaded as Styles)?.Resources;
            }

            if (style is Styles nested && Find(nested) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Recolor(object? value, ThemeSpec theme, (Geometry From, Geometry To)? motif, (Geometry From, Geometry To)? motifAlt)
    {
        switch (value)
        {
            case SolidColorBrush solid:
                solid.Color = Map(Original(solid, solid.Color), theme);
                break;
            case GradientBrush gradient:
                foreach (var stop in gradient.GradientStops)
                {
                    stop.Color = Map(Original(stop, stop.Color), theme);
                }

                break;
            case DrawingBrush drawing:
                Recolor(drawing.Drawing, theme, motif, motifAlt);
                break;
            case DrawingGroup group:
                foreach (var child in group.Children)
                {
                    Recolor(child, theme, motif, motifAlt);
                }

                break;
            case GeometryDrawing shape:
                var geometry = OriginalGeometry.GetValue(shape, s => s.Geometry!);
                shape.Geometry = motif is { } a && ReferenceEquals(geometry, a.From) ? a.To
                    : motifAlt is { } b && ReferenceEquals(geometry, b.From) ? b.To
                    : geometry;
                Recolor(shape.Brush, theme, motif, motifAlt);
                Recolor(shape.Pen, theme, motif, motifAlt);
                break;
            case Pen pen:
                Recolor(pen.Brush, theme, motif, motifAlt);
                break;
        }
    }

    private static Color Original(object owner, Color current) => Originals.GetValue(owner, _ => new StrongBox<Color>(current)).Value;

    private static Color Map(Color color, ThemeSpec theme) => ToColor(ThemePalette.Map(color.ToUInt32(), theme));

    private static Color ToColor(uint argb) => Color.FromUInt32(argb);

    /// <summary>Controls that draw themselves (charts, rules, ornaments) read brushes while rendering: draw them again.</summary>
    private static void Repaint(Visual root)
    {
        root.InvalidateVisual();
        foreach (var visual in root.GetVisualDescendants())
        {
            visual.InvalidateVisual();
        }
    }
}
