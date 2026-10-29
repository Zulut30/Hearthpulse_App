using System.Collections;
using System.Globalization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Infrastructure;
#else
namespace Hearthpulse.UI.Infrastructure;
#endif

/// <summary>
/// The rules behind both UIs' value converters (<c>Converters.cs</c>): what counts as shown, which theme brush a value
/// takes and how wide a share bar is. The converters only look the brush up or wrap the result for their framework.
/// </summary>
internal static class DisplayRules
{
    /// <summary>True when the value is something to show: true, non-zero, non-empty, non-null.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        double d => d != 0,
        string s => s.Length > 0,
        ICollection c => c.Count > 0,
        _ => true,
    };

    /// <summary>The theme brush key of a tier letter, a rarity or a result; "Muted" for anything else.</summary>
    public static string BrushKey(string? value) => value switch
    {
        "S" => "TierS",
        "A" => "TierA",
        "B" => "TierB",
        "C" => "TierC",
        "D" or "E" => "TierD",
        "COMMON" or "FREE" => "RarityCommon",
        "RARE" => "RarityRare",
        "EPIC" => "RarityEpic",
        "LEGENDARY" => "RarityLegendary",
        "Win" or "Won" or "CORE" => "Positive",
        "Loss" or "Lost" => "Negative",
        "ADDON" => "Info",
        _ => "Muted",
    };

    /// <summary>
    /// The brush key of a placement or a signed number. A numeric <paramref name="parameter"/> is the top-N cutoff for
    /// placements (1..N positive, above it negative, 0 muted); "text" gives zero the normal text colour instead of muted.
    /// </summary>
    public static string SignKey(object? value, object? parameter)
    {
        var zero = parameter as string == "text" ? "Text" : "Muted";
        if (parameter is string cut && int.TryParse(cut, CultureInfo.InvariantCulture, out var top) && value is int place)
        {
            return place <= 0 ? "Muted" : place <= top ? "Positive" : "Negative";
        }

        if (value is IConvertible c)
        {
            var d = c.ToDouble(CultureInfo.InvariantCulture);
            return d > 0 ? "Positive" : d < 0 ? "Negative" : zero;
        }

        return zero;
    }

    /// <summary>A 0..1 share as a width; <paramref name="parameter"/> is the full width (100 when it is not a number).</summary>
    public static double FractionWidth(object? value, object? parameter)
    {
        var full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 100;
        var fraction = value is IConvertible c ? Math.Clamp(c.ToDouble(CultureInfo.InvariantCulture), 0, 1) : 0;
        return Math.Max(fraction * full, 0);
    }
}
