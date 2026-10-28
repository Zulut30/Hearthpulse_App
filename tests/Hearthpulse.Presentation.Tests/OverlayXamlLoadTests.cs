using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The shipped WPF overlay resolves its resources only at run time: a missing key in a panel or in OverlayLook.xaml
/// builds fine and fails when the overlay opens. This loads the overlay window with the theme, in layout mode, and lays
/// it out off screen (nothing is shown), so every panel's template and the placeholders are built once.
/// </summary>
public sealed class OverlayXamlLoadTests
{
    [Fact]
    public void Wpf_overlay_loads_with_the_theme_and_lays_out_its_placeholders()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Overlay XAML failed to load", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // From the theme source embedded in the app, as for a non-default theme (a relative pack URI would point at the test).
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var model = new OverlayViewModel { LayoutEditing = true };
        var window = new OverlayWindow(model, new AppSettings());
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1920, 1080));
        root.Arrange(new Rect(0, 0, 1920, 1080));
        // Bindings attach at DataBind priority: let them, then lay out again with the placeholders shown.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();

        var panels = Descendants<MovablePanel>(root).ToDictionary(p => p.Id);
        Assert.Contains("arena-draft", panels.Keys);
        // Layout mode: every panel shows its placeholder; the wide pick rows keep theirs to one side.
        Assert.All(panels.Values, p => Assert.Equal(Visibility.Visible, p.Visibility));
        Assert.Equal(HorizontalAlignment.Left, panels["hero-row"].PlaceholderAlignment);

        // Measured with real fonts: no placeholder frame (4 units around it) lies on another one.
        var frames = panels.Values
            .Select(p => (p.Id, Part: (FrameworkElement)p.Template.FindName("Placeholder", p)))
            .Select(p => (p.Id, Rect: new Rect(p.Part.TranslatePoint(default, root), p.Part.RenderSize)))
            .ToList();
        var overlaps = new List<string>();
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.True(frames[i].Rect.Width > 0, frames[i].Id);
            for (var j = i + 1; j < frames.Count; j++)
            {
                var a = frames[i].Rect;
                var b = frames[j].Rect;
                a.Inflate(3, 3);
                if (a.IntersectsWith(b) && Rect.Intersect(a, b) is { Width: > 3, Height: > 3 })
                {
                    overlaps.Add($"{frames[i].Id} {frames[i].Rect} / {frames[j].Id} {frames[j].Rect}");
                }
            }
        }

        Assert.True(overlaps.Count == 0, string.Join(Environment.NewLine, overlaps));

        // The Arena draft panel with its own merged OverlayLook dictionary.
        var arena = new ArenaDraftPanel { DataContext = model };
        arena.Measure(new Size(400, 1000));
        Assert.True(arena.DesiredSize.Height > 0);

        // Panels that merge OverlayLook for their headers, built on their own (outside the window's resources).
        foreach (var panel in new FrameworkElement[] { new SecretsPanel(), new OpponentArchetypePanel(), new LadderSessionPanel(), new CardPoolPanel(), new CardPoolBrowser() })
        {
            panel.DataContext = model;
            panel.Measure(new Size(400, 1080));
            Assert.True(panel.DesiredSize.Height > 0, panel.GetType().Name);
        }

        // A deck row with no copies left whose count just moved: the dimming setters and the flash storyboard resolve.
        var row = new System.Windows.Controls.ContentPresenter
        {
            Content = new DeckTile("CS2_029", "Fireball", 4, "0", true, true, "COMMON", string.Empty, false, Changed: true),
            ContentTemplate = (DataTemplate)window.FindResource("DeckTileTemplate"),
        };
        ((System.Windows.Controls.Panel)root).Children.Add(row);
        row.Measure(new Size(240, 30));
        row.Arrange(new Rect(0, 0, 240, 30));
        row.UpdateLayout();
        Assert.True(row.ActualHeight > 0);

        // Ctrl over a panel during a game: it takes the mouse and shows its frame and drag hint; released, clicks go
        // through to the game again.
        model.LayoutEditing = false;
        var deck = panels["deck"];
        Assert.False(OverlayHit.GetInteractive(deck));
        deck.IsDragArmed = true;
        Assert.True(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Visible, ((FrameworkElement)deck.Template.FindName("Shield", deck)).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)deck.Template.FindName("ArmedHint", deck)).Visibility);
        // Its tag buttons show too (compact only where the panel has card lists), and the mouse may go over to them.
        // Only a panel the game shows is ever armed.
        deck.Shown = true;
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, ((FrameworkElement)deck.Template.FindName("Tools", deck)).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)deck.Template.FindName("PART_Compact", deck)).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)panels["counters"].Template.FindName("PART_Compact", panels["counters"])).Visibility);
        Assert.True(deck.ArmedArea().Top < -20 && deck.ArmedArea().Bottom >= deck.ActualHeight - 0.5, deck.ArmedArea().ToString());

        // The player's look: the content fades, the frame does not; compact rows are short and without art.
        deck.ApplyLook(new PanelLook { Opacity = 0.55, Compact = true });
        Assert.Equal(0.55, ((FrameworkElement)deck.Template.FindName("Body", deck)).Opacity, 3);
        Assert.Equal(1, ((FrameworkElement)deck.Template.FindName("Frame", deck)).Opacity);
        MovablePanel.SetCompact(row, true);
        row.UpdateLayout();
        var rowRoot = Descendants<System.Windows.Controls.Grid>(row).First(g => g.Name == "RowRoot");
        Assert.Equal(18, rowRoot.ActualHeight, 0.5); // layout rounding to the test machine's pixels
        Assert.Equal(Visibility.Collapsed, Descendants<System.Windows.Controls.Image>(row).First(i => i.Name == "Art").Visibility);
        deck.ApplyLook(null);
        Assert.False(MovablePanel.GetCompact(deck));
        deck.IsDragArmed = false;
        Assert.False(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)deck.Template.FindName("ArmedHint", deck)).Visibility);
        window.Close();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
