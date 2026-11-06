using System.Windows.Media;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The app's "reduce motion" setting: in WPF the theme is rebuilt with every motion token at zero and code-started
/// motion (page reveal, the selection pill) is instant; the Avalonia twin is in Hearthpulse.Tests (ThemeFileTests).
/// </summary>
[Collection(MotionCollection.Name)]
public sealed class MotionTests
{
    [Fact]
    public void Reduced_motion_builds_the_wpf_theme_with_instant_timings()
    {
        var saved = Motion.Reduced;
        try
        {
            Motion.Reduced = true;
            Assert.False(Motion.On);
            var xaml = ThemeEngine.Source(ThemePalette.Resolve(ThemePalette.DefaultName, 0, 0, 0));
            Assert.Contains("<Duration x:Key=\"MotionFast\">0:0:0</Duration>", xaml, StringComparison.Ordinal);
            Assert.Contains("<Duration x:Key=\"MotionSettle\">0:0:0</Duration>", xaml, StringComparison.Ordinal);
            Assert.Contains("<Duration x:Key=\"MotionEnter\">0:0:0</Duration>", xaml, StringComparison.Ordinal);
        }
        finally
        {
            Motion.Reduced = saved;
        }
    }

    [Fact]
    public void Reduced_motion_shows_a_wpf_page_at_once() => WpfThread.RunFresh(() =>
    {
        var saved = Motion.Reduced;
        try
        {
            Motion.Reduced = true;
            var page = new System.Windows.Controls.Border { Opacity = 0.3, RenderTransform = new TranslateTransform(0, 10) };
            Motion.Reveal(page);
            Assert.Equal(1, page.Opacity);
            Assert.Equal(0, ((TranslateTransform)page.RenderTransform).Y);

            var pill = new TranslateTransform();
            Motion.Glide(pill, 42);
            Assert.Equal(42, pill.Y);
        }
        finally
        {
            Motion.Reduced = saved;
        }
    });
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MotionCollection
{
    public const string Name = "Motion";
}
