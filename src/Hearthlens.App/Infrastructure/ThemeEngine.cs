using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Hearthlens.Core;

namespace Hearthlens.App.Infrastructure;

/// <summary>
/// Builds the app's resource dictionary for a colour theme. The source of Themes/Theme.xaml ships inside the app;
/// every colour in it is recoloured through <see cref="ThemePalette.Map"/> and the result replaces the theme.
/// Windows resolve their brushes when they are created, so the host recreates them after a change.
/// </summary>
public static partial class ThemeEngine
{
    private static readonly Uri BuiltIn = new("pack://application:,,,/Themes/Theme.xaml");
    private static string? _source;

    /// <summary>Brushes whose colour carries meaning never change.</summary>
    [GeneratedRegex("x:Key=\"(Positive|Negative|Info|Rarity|Tier|ManaGem)")]
    private static partial Regex FixedKey();

    [GeneratedRegex("#([0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\\b")]
    private static partial Regex HexColor();

    [GeneratedRegex("clr-namespace:([\\w.]+)\"")]
    private static partial Regex LocalNamespace();

    public static void Apply(Application app, ThemeSpec theme)
    {
        var dictionary = ThemePalette.IsDefault(theme) ? new ResourceDictionary { Source = BuiltIn } : Build(theme);
        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(dictionary);
    }

    public static ResourceDictionary Build(ThemeSpec theme)
    {
        _source ??= Load();
        var lines = _source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!FixedKey().IsMatch(lines[i]))
            {
                lines[i] = HexColor().Replace(lines[i], m => Recolor(m.Value, theme));
            }
        }

        var xaml = LocalNamespace().Replace(string.Join('\n', lines), "clr-namespace:$1;assembly=Hearthlens\"");
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    /// <summary>One theme colour as a frozen brush, for previews.</summary>
    public static SolidColorBrush Brush(uint argb, ThemeSpec theme)
    {
        var c = ThemePalette.Map(argb, theme);
        var brush = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        brush.Freeze();
        return brush;
    }

    private static string Recolor(string hex, ThemeSpec theme)
    {
        var value = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (hex.Length == 7)
        {
            value |= 0xFF000000;
        }

        var mapped = ThemePalette.Map(value, theme);
        return hex.Length == 7 ? $"#{mapped & 0xFFFFFF:X6}" : $"#{mapped:X8}";
    }

    private static string Load()
    {
        using var stream = typeof(ThemeEngine).Assembly.GetManifestResourceStream("Hearthlens.Theme.xaml")
            ?? throw new InvalidOperationException("Theme source is missing from the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
