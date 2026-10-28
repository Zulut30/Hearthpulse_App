using System.Globalization;
using WpfFormat = Hearthpulse.App.Overlay.OverlayFormat;
using AvaFormat = Hearthpulse.UI.Overlay.OverlayFormat;

namespace Hearthpulse.Presentation.Tests;

/// <summary>Every overlay percentage in the culture's one pattern: no "12.8%" next to "55,2 %" any more.</summary>
public sealed class OverlayFormatTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void Overlay_percentages_follow_the_culture_in_both_hosts(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            foreach (var (percent, signed, above, below) in new (Func<double, int, string>, Func<double, int, string>, Func<double, int, string>, Func<double, int, string>)[]
                     {
                         (WpfFormat.Percent, WpfFormat.SignedPercent, WpfFormat.Above, WpfFormat.Below),
                         (AvaFormat.Percent, AvaFormat.SignedPercent, AvaFormat.Above, AvaFormat.Below),
                     })
            {
                Assert.Equal(0.552.ToString("P1", culture), percent(0.552, 1));
                Assert.Equal(0.08.ToString("P0", culture), percent(0.08, 0));
                Assert.Equal("+" + 0.021.ToString("P1", culture), signed(0.021, 1));
                // A true minus sign, and no sign for a change that rounds to nothing.
                Assert.Equal("−" + 0.007.ToString("P1", culture), signed(-0.007, 1));
                Assert.Equal(0.0.ToString("P1", culture), signed(-0.0004, 1));
                Assert.Equal(">" + 0.99.ToString("P0", culture), above(0.99, 0));
                Assert.Equal("<" + 0.001.ToString("P1", culture), below(0.001, 1));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
