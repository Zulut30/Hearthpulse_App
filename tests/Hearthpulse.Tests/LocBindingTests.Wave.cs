using Avalonia;
using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The gold rule is as tall as its diamond plus a pixel (9), at any width; 40 wide when the row gives it no width.
    /// (Q6 X6: the old wave's amplitude and thickness, never set, are gone without changing a row's height.)
    /// </summary>
    [Fact]
    public void Wave_rule_is_nine_high_and_takes_the_given_width() => Headless.Run(() =>
    {
        var wave = new Wave();
        wave.Measure(new Size(300, 50));
        Assert.Equal(new Size(300, 9), wave.DesiredSize);
        wave.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(new Size(40, 9), wave.DesiredSize);
    });
}
