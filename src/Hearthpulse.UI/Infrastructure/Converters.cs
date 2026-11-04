using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>Looks up theme brushes by key from the running application.</summary>
public static class ThemeBrushes
{
    public static IBrush Get(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Gray;
}

/// <summary>True when the value is truthy: true, non-zero, non-empty, non-null. Parameter "!" inverts.</summary>
/// <summary>
/// A local image file (a cover the host downloaded into the cache) to a bitmap decoded to <see cref="Width"/> pixels;
/// null for no file or one that cannot be read.
/// </summary>
public sealed class LocalImage : IValueConverter
{
    private const int Width = 640;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, Width);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Hearthpulse.Core.Diag.Info("cover image unreadable: " + ex.Message);
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class Truthy : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DisplayRules.Truthy(value) ^ (parameter as string == "!");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps keys (tier letters, rarities, results) to theme brushes (<see cref="DisplayRules.BrushKey"/>).</summary>
public sealed class KeyBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Get(DisplayRules.BrushKey(value?.ToString()));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Placement (1-8) or signed number to positive/negative/muted brush (<see cref="DisplayRules.SignKey"/>). A numeric
/// parameter is the top-N cutoff for placements; "text" makes zero use the normal text colour instead of muted.
/// </summary>
public sealed class SignBrush : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ThemeBrushes.Get(DisplayRules.SignKey(value, parameter));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0..1 fraction to a width; parameter is the full width.</summary>
public sealed class FractionWidth : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => DisplayRules.FractionWidth(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A number to a left margin, for items placed at an x offset inside an overlapping panel.</summary>
public sealed class LeftMargin : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
