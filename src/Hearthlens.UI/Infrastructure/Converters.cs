using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Hearthlens.UI.Infrastructure;

/// <summary>Looks up theme brushes by key from the running application.</summary>
public static class ThemeBrushes
{
    public static IBrush Get(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}

/// <summary>True when the value is truthy: true, non-zero, non-empty, non-null. Parameter "!" inverts.</summary>
public sealed class Truthy : IValueConverter
{
    public static bool Test(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Test(value) ^ (parameter as string == "!");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps keys (tier letters, rarities, results) to theme brushes.</summary>
public sealed class KeyBrush : IValueConverter
{
    public static string KeyFor(string? value) => value switch
    {
        "S" => "TierS",
        "A" => "TierA",
        "B" => "TierB",
        "C" => "TierC",
        "D" => "TierD",
        "COMMON" or "FREE" => "RarityCommon",
        "RARE" => "RarityRare",
        "EPIC" => "RarityEpic",
        "LEGENDARY" => "RarityLegendary",
        "Win" or "Won" or "CORE" => "Positive",
        "Loss" or "Lost" => "Negative",
        "ADDON" => "Info",
        _ => "Muted",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Get(KeyFor(value?.ToString()));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Placement (1-8) or signed number to positive/negative/muted brush. A numeric parameter is the top-N cutoff for
/// placements; "text" makes zero use the normal text colour instead of muted.
/// </summary>
public sealed class SignBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var zero = parameter as string == "text" ? "Text" : "Muted";
        var key = zero;
        if (parameter is string cut && int.TryParse(cut, out var top) && value is int place)
        {
            key = place <= 0 ? "Muted" : place <= top ? "Positive" : "Negative";
        }
        else if (value is IConvertible c)
        {
            var d = c.ToDouble(CultureInfo.InvariantCulture);
            key = d > 0 ? "Positive" : d < 0 ? "Negative" : zero;
        }

        return ThemeBrushes.Get(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 fraction to a width; parameter is the full width.</summary>
public sealed class FractionWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100;
        var fraction = value is IConvertible c ? Math.Clamp(c.ToDouble(CultureInfo.InvariantCulture), 0, 1) : 0;
        return Math.Max(fraction * full, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A number to a left margin, for items placed at an x offset inside an overlapping panel.</summary>
public sealed class LeftMargin : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
