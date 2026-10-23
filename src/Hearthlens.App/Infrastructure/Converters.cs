using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hearthlens.App.Infrastructure;

/// <summary>Visible when the value is truthy: true, non-zero, non-empty, non-null. Parameter "!" inverts.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = Truthy(value) ^ (parameter as string == "!");
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps keys (tier letters, rarities, results) to theme brushes.</summary>
public sealed class KeyBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
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
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Placement (1-8) or signed number to positive/negative/muted brush. Parameter = top-N cutoff for placements.</summary>
public sealed class SignBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = "Muted";
        if (parameter is string cut && int.TryParse(cut, out var top) && value is int place)
        {
            key = place <= 0 ? "Muted" : place <= top ? "Positive" : "Negative";
        }
        else if (value is IConvertible c)
        {
            var d = c.ToDouble(CultureInfo.InvariantCulture);
            key = d > 0 ? "Positive" : d < 0 ? "Negative" : "Muted";
        }

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a signed integer as "+12" / "−8".</summary>
public sealed class Signed : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int i when i > 0 => $"+{i:N0}",
        int i when i < 0 => $"−{-i:N0}",
        int => "±0",
        double d when d > 0 => $"+{d:0.00}",
        double d when d < 0 => $"−{-d:0.00}",
        _ => "—",
    };

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
