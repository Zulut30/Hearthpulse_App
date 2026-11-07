using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The Avalonia overlay's panel tag at the screen's edges (AC8): the session panel at its default spot, 10 from the
    /// top at the right edge, armed with Ctrl, shows its whole tag under itself, lined up with its right side and on the
    /// screen, with the lock in the corner; moved to the middle, the tag is above it at its left edge again.
    /// </summary>
    [Fact]
    public void Avalonia_panel_tag_goes_under_a_panel_at_the_top_and_lines_up_right_at_the_right_edge() => Headless.Run(PanelTagAtEdges);

    private static void PanelTagAtEdges()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1920, Height = 1080 };
        window.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        try
        {
            var model = new OverlayViewModel();
            var view = new OverlayView { DataContext = model };
            window.Content = view;
            window.Show();
            view.Fit(1920, 1080);
            var session = view.GetLogicalDescendants().OfType<MovablePanel>().Single(p => p.Id == "session");
            session.Shown = true;
            Settle();
            Assert.True(session.Bounds.Width > 0);

            Assert.True(view.UpdateDragArm(Inside(), controlDown: true, ArmClock));
            Settle();
            Settle();
            var chrome = Part("PART_Chrome");
            var row = Part("PART_TagRow");
            var tag = Rect(chrome);
            var panel = Rect(session);
            Assert.True(chrome.IsVisible);
            // Under the panel, its row 8 below it, all of it on the screen.
            Assert.True(tag.Top >= panel.Bottom, $"{tag} {panel}");
            Assert.Equal(panel.Bottom + 8, Rect(row).Top, 1);
            Assert.True(tag.Left >= 0 && tag.Right <= 1920 && tag.Bottom <= 1080, tag.ToString());
            // Lined up with the panel's right side (the frame reaches 4 past it); the lock comes last, in the corner.
            Assert.Equal(panel.Right + 4, tag.Right, 1);
            Assert.Equal("PART_Lock", ((Panel)row).Children[^1].Name);

            // In the middle of the screen: above the panel at its left edge, in the usual order.
            session.Apply(new PanelPlacement { X = 0.4, Y = 0.5, Scale = 1 }, new Size(1920, 1080));
            Settle();
            Settle();
            tag = Rect(chrome);
            panel = Rect(session);
            Assert.Equal(panel.Top - PanelLayout.TagHeight, tag.Top, 1);
            Assert.Equal(panel.Left - PanelLayout.TagInset, tag.Left, 1);
            Assert.Equal("PART_Lock", ((Panel)row).Children[0].Name);

            Point Inside() => session.TranslatePoint(new Point(6, 6), view)!.Value;

            Rect Rect(Control part) =>
                new(part.TranslatePoint(default, view)!.Value, part.TranslatePoint(new Point(part.Bounds.Width, part.Bounds.Height), view)!.Value);

            Control Part(string name) => Assert.Single(session.GetVisualDescendants().OfType<Control>(), c => c.Name == name);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }

        void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
