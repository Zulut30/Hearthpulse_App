using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Hearthpulse.App.Infrastructure;

/// <summary>Visible when the value is truthy: true, non-zero, non-empty, non-null. Parameter "!" inverts.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = DisplayRules.Truthy(value) ^ (parameter as string == "!");
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps keys (tier letters, rarities, results) to theme brushes (<see cref="DisplayRules.BrushKey"/>).</summary>
public sealed class KeyBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(DisplayRules.BrushKey(value?.ToString())) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Placement (1-8) or signed number to positive/negative/muted brush (<see cref="DisplayRules.SignKey"/>).</summary>
public sealed class SignBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(DisplayRules.SignKey(value, parameter)) as Brush ?? Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 fraction to a width; parameter is the full width.</summary>
public sealed class FractionWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => DisplayRules.FractionWidth(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
