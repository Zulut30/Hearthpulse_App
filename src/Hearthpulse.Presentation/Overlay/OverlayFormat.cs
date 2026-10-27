using System.Globalization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Percentages on the overlay, all in the culture's own pattern ("55.2%" in English, "55,2 %" in Russian), so the panels
/// never mix "12.8%" and "55,2 %" next to each other.
/// </summary>
public static class OverlayFormat
{
    /// <summary>0.552 → "55%" (or "55.2%" with <paramref name="decimals"/> 1).</summary>
    public static string Percent(double share, int decimals = 0) =>
        share.ToString(decimals == 0 ? "P0" : "P" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    /// <summary>A change in win rate: 0.007 → "+0.7%", −0.007 → "−0.7%", 0 → "0.0%".</summary>
    public static string SignedPercent(double delta, int decimals = 1)
    {
        var text = Percent(Math.Abs(delta), decimals);
        var unit = Math.Pow(10, -(decimals + 2)) / 2;
        return delta >= unit ? "+" + text : delta <= -unit ? "−" + text : text;
    }

    /// <summary>"&gt;99%" / "&lt;0.1%": a bound for an estimate that must not read as a certainty.</summary>
    public static string Above(double share, int decimals = 0) => ">" + Percent(share, decimals);

    /// <inheritdoc cref="Above"/>
    public static string Below(double share, int decimals = 0) => "<" + Percent(share, decimals);
}
