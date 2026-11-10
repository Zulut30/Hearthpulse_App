using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthpulse.App.Main.Meta;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The meta filters' segment (MetaSegment: one plate and a label) is drawn as the theme's Segment (a Grid and three
/// Borders) is: the same size, and the same pixels for a plain and for a chosen segment, antialiased edges aside.
/// </summary>
public sealed class MetaSegmentLookTests
{
    [Theory]
    [InlineData(false, "Легенда", "")]
    [InlineData(true, "Легенда", "")]
    [InlineData(true, "1 000", "")]
    [InlineData(false, "Легенда", "hover")]
    [InlineData(true, "Легенда", "hover")]
    [InlineData(false, "Легенда", "pressed")]
    public void The_light_segment_looks_as_the_themes_segment(bool chosen, string label, string pointer) => WpfThread.RunThemed(() =>
    {
        var light = (Style)new ConstructedMetaView().FindResource("MetaSegment");
        var theme = (Style)Application.Current.FindResource("Segment");

        var (expected, size) = Shot(theme, chosen, label, pointer);
        var (actual, lightSize) = Shot(light, chosen, label, pointer);

        Assert.Equal(size, lightSize);
        Assert.Equal(expected.Length, actual.Length);
        var lit = 0;
        var different = 0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            lit += expected[i + 3] > 0 ? 1 : 0;
            var apart = Math.Max(Math.Max(Math.Abs(expected[i] - actual[i]), Math.Abs(expected[i + 1] - actual[i + 1])),
                Math.Max(Math.Abs(expected[i + 2] - actual[i + 2]), Math.Abs(expected[i + 3] - actual[i + 3])));
            different += apart > 48 ? 1 : 0;
        }

        // The chosen segment's line is there in both, full length (the segment less 4 on each side), on the pixel row
        // of the segment's bottom edge; a plain segment has none.
        var stride = expected.Length / (int)Math.Ceiling(size.Height + 12);
        var bottom = (int)Math.Round(6 + size.Height);
        foreach (var pixels in new[] { expected, actual })
        {
            var line = Enumerable.Range(bottom - 3, 5).Max(row => Enumerable.Range(0, stride / 4).Count(x => pixels[(row * stride) + (x * 4) + 3] > 128));
            Assert.True(chosen ? line >= size.Width - 12 && line <= size.Width - 6 : line == 0, $"the line is {line} pixels long, the segment {size.Width} wide");
        }

        // Under the pointer the tint fills the segment in both (its rounded top corners aside).
        if (pointer.Length > 0)
        {
            Assert.True(lit > size.Width * size.Height * 0.9, $"{lit} pixels drawn of {size.Width * size.Height:0}");
        }

        // Something is drawn at all (the label; the line when chosen), and no more than a few edge pixels differ.
        Assert.True(lit > 100, $"{lit} pixels drawn");
        Assert.True(different <= lit / 50, $"{different} of {lit} drawn pixels differ");
    });

    /// <summary>The segment alone on a transparent ground with room around it (the line lies a pixel under it), after its motion.</summary>
    private static (byte[] Pixels, Size Size) Shot(Style style, bool chosen, string label, string pointer)
    {
        var segment = new RadioButton { Style = style, Content = label };
        var ground = new Border { Padding = new Thickness(6), Child = segment, UseLayoutRounding = true, SnapsToDevicePixels = true };
        TextOptions.SetTextFormattingMode(ground, TextFormattingMode.Display);
        ground.Measure(new Size(400, 200));
        ground.Arrange(new Rect(ground.DesiredSize));

        // Chosen once its template is there, as a click does: the template's trigger starts the line's motion.
        segment.IsChecked = chosen;

        // The pointer cannot be put over a segment in a test: what the template's triggers do under it, and when
        // pressed, is set on the template's parts - the tint at full strength (pressed: the pressed tint), the label lit.
        if (pointer.Length > 0)
        {
            var tint = (Brush)Application.Current.FindResource(pointer == "pressed" ? "Pressed" : "Hover");
            if (segment.Template.FindName("Plate", segment) is Hearthpulse.App.Controls.SegmentPlate plate)
            {
                plate.HoverBrush = tint;
                plate.HoverOpacity = 1;
            }
            else
            {
                var layer = (Border)segment.Template.FindName("HoverLayer", segment);
                layer.Background = tint;
                layer.Opacity = 1;
            }

            if (!chosen)
            {
                segment.Foreground = (Brush)Application.Current.FindResource("Text");
            }
        }

        // The line grows over the theme's settle time: let it finish, then lay out and draw what it left.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        ground.UpdateLayout();

        var width = (int)Math.Ceiling(ground.ActualWidth);
        var height = (int)Math.Ceiling(ground.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(ground);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, new Size(segment.ActualWidth, segment.ActualHeight));
    }
}
