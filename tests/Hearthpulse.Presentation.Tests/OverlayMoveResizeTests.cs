using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Panels move and resize during a game while Ctrl is held over them: the corner marker's size steps, the one-time tip
/// in both view-model flavours, and the WPF panel template (marker, hint, a drag that loses the mouse).
/// </summary>
public sealed class OverlayMoveResizeTests
{
    private delegate double CornerScale(double scale, double width, double height, double dx, double dy);

    [Fact]
    public void Corner_drag_resizes_along_the_diagonal_in_wheel_steps_within_the_limits()
    {
        foreach (var corner in new CornerScale[] { Hearthpulse.App.Overlay.PanelSnap.CornerScale, Hearthpulse.UI.Overlay.PanelSnap.CornerScale })
        {
            Assert.Equal(1, corner(1, 200, 100, 0, 0));
            Assert.Equal(1.2, corner(1, 200, 100, 40, 20), 3);      // the corner moved a fifth of the way along the diagonal
            Assert.Equal(1.15, corner(1, 200, 100, 40, 0), 3);      // sideways only: 1.16, to the 5 % step
            Assert.Equal(1.15, corner(1.3, 260, 130, -26, -13), 3); // smaller from 130 %: 1.17, to the step
            Assert.Equal(0.6, corner(1, 200, 100, -500, -500));     // never below the wheel's limits
            Assert.Equal(1.6, corner(1, 200, 100, 900, 900));
            Assert.Equal(1.3, corner(1.3, 0, 0, 40, 20));           // nothing laid out yet: unchanged
        }
    }

    [Fact]
    public void The_move_tip_shows_once_over_the_game_away_from_the_toast_and_waits_out_layout_mode()
    {
        foreach (var make in new Func<(Func<AppSettings, bool> Offer, Func<(bool Tip, bool Toast)> Shown, Action<bool> Layout)>[]
                 {
                     () => { var vm = new WpfOverlay(); return (vm.OfferMoveTip, () => (vm.ShowMoveTip, vm.ShowToast), v => vm.LayoutEditing = v); },
                     () => { var vm = new AvaOverlay(); return (vm.OfferMoveTip, () => (vm.ShowMoveTip, vm.ShowToast), v => vm.LayoutEditing = v); },
                 })
        {
            var settings = new AppSettings();
            var first = make();
            first.Layout(true);
            Assert.False(first.Offer(settings));
            Assert.False(settings.OverlayMoveTipShown);
            Assert.Equal((false, false), first.Shown());

            // Its own spot at the top centre (ShowMoveTip), not the end-of-game toast over the hand.
            first.Layout(false);
            Assert.True(first.Offer(settings));
            Assert.True(settings.OverlayMoveTipShown);
            Assert.Equal((true, false), first.Shown());

            // The flag is in the settings: a new overlay (theme change, next start) does not show it again.
            var next = make();
            Assert.False(next.Offer(settings));
            Assert.Equal((false, false), next.Shown());
        }

        Assert.True(AppSettings.FromJson(new AppSettings { OverlayMoveTipShown = true }.ToJson()).OverlayMoveTipShown);
        Assert.False(AppSettings.FromJson("{}").OverlayMoveTipShown);
    }

    [Fact]
    public void Wpf_panel_shows_its_corner_marker_and_move_hint_and_ends_a_drag_that_lost_the_mouse() => Sta(WpfPanel);

    private static void WpfPanel()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var model = new WpfOverlay();
        var window = new OverlayWindow(model, new AppSettings());
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1920, 1080));
        root.Arrange(new Rect(0, 0, 1920, 1080));
        Idle();
        root.UpdateLayout();
        var panels = Descendants<MovablePanel>(root).ToDictionary(p => p.Id);
        var deck = panels["deck"];
        deck.Shown = true;
        root.UpdateLayout();
        FrameworkElement Part(MovablePanel panel, string name) => (FrameworkElement)panel.Template.FindName(name, panel);

        // A shown panel during a game: no marker until Ctrl arms it.
        Assert.Equal(Visibility.Collapsed, Part(deck, "PART_Grip").Visibility);
        deck.IsDragArmed = true;
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, Part(deck, "PART_Grip").Visibility);
        Assert.Equal(Visibility.Visible, Part(deck, "Frame").Visibility);
        var hint = (TextBlock)Part(deck, "ArmedHint");
        Assert.Equal(Visibility.Visible, hint.Visibility);
        Assert.Equal("Drag to move · corner or wheel to resize · right-click to reset", hint.Text);
        // The marker sits just outside the corner: the mouse over it keeps the panel armed.
        var area = deck.ArmedArea();
        Assert.True(area.Right > deck.ActualWidth + 3 && area.Bottom > deck.ActualHeight + 3, area.ToString());

        // The marker is one size and sticks out of the corner as far at every panel size, as in the Avalonia overlay.
        var surface = (FrameworkElement)deck.Parent;
        foreach (var scale in new[] { 0.6, 1.6 })
        {
            deck.Apply(new PanelPlacement { X = 0.3, Y = 0.1, Scale = scale }, new Size(1920, 1080));
            root.UpdateLayout();
            var grip = Part(deck, "PART_Grip");
            var marker = grip.TransformToAncestor(surface).TransformBounds(new Rect(grip.RenderSize));
            var panel = deck.TransformToAncestor(surface).TransformBounds(new Rect(deck.RenderSize));
            // To whole units: layout rounding snaps to the test machine's pixels.
            Assert.Equal(14, marker.Width, 0);
            Assert.Equal(14, marker.Height, 0);
            Assert.Equal(6, marker.Right - panel.Right, 0);
            Assert.Equal(6, marker.Bottom - panel.Bottom, 0);
        }

        deck.Apply(null, new Size(1920, 1080));
        root.UpdateLayout();

        // The marker pressed on its icon resizes: a fifth of the way out along the diagonal makes 120 %, the top-left
        // corner stays, and the size is saved like a wheel step. With no window the press reads the mouse at (0, 0)
        // (no presentation source), so the step goes straight to DragTo.
        PanelPlacement? saved = null;
        deck.PlacementChanged += (_, placement) => saved = placement;
        var before = deck.TranslatePoint(default, surface);
        var icon = Descendants<TextBlock>(Part(deck, "PART_Grip")).Single();
        // As a real click: PreviewMouseDown tunnels down to the icon, and each element on the way raises its own
        // PreviewMouseLeftButtonDown (a direct event) with the icon as the original source.
        icon.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.True(deck.IsDragging);
        Assert.True(deck.IsResizing);
        deck.DragTo(new Point(deck.ActualWidth * 0.2, deck.ActualHeight * 0.2));
        root.UpdateLayout();
        Assert.Equal(PanelSnap.ScaleText(1.2), deck.ScaleText);
        var after = deck.TranslatePoint(default, surface);
        Assert.Equal(before.X, after.X, 0);
        Assert.Equal(before.Y, after.Y, 0);
        deck.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Assert.False(deck.IsDragging);
        Assert.Equal(1.2, saved!.Scale, 3);
        Assert.Equal(before.X / surface.ActualWidth, saved.X, 3);
        Assert.Equal(before.Y / surface.ActualHeight, saved.Y, 3);
        deck.Apply(null, new Size(1920, 1080));
        root.UpdateLayout();
        saved = null;

        // A drag that loses the mouse (the panel hid, the player switched windows) ends and saves where the panel is.
        deck.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Assert.True(deck.IsDragging);
        Assert.False(deck.IsResizing);
        deck.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Assert.False(deck.IsDragging);
        Assert.NotNull(saved);
        Assert.Equal(1, saved!.Scale);

        // Released Ctrl: back to letting clicks through, no marker.
        deck.IsDragArmed = false;
        Assert.False(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Collapsed, Part(deck, "PART_Grip").Visibility);

        // The one-time tip: its own card at the top centre, clear of the hand, never taking the mouse.
        var tip = (FrameworkElement)window.FindName("MoveTip");
        Assert.NotEqual(Visibility.Visible, tip.Visibility);
        Assert.True(model.OfferMoveTip(new AppSettings()));
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, tip.Visibility);
        Assert.Equal((HorizontalAlignment.Center, VerticalAlignment.Top), (tip.HorizontalAlignment, tip.VerticalAlignment));
        Assert.True(tip.TranslatePoint(default, surface).Y < 40);
        Assert.False(tip.IsHitTestVisible);
        Assert.Contains("Hold Ctrl over a panel to move and resize it", Descendants<TextBlock>(tip).Select(t => t.Text));
        Assert.False(model.ShowToast);

        // Layout mode: the marker on every shown panel, none on the placeholders.
        model.LayoutEditing = true;
        root.UpdateLayout();
        Assert.Equal(Visibility.Visible, Part(deck, "PART_Grip").Visibility);
        Assert.Equal(Visibility.Collapsed, Part(panels["opponent"], "PART_Grip").Visibility);
        window.Close();
    }

    private static void Idle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
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
            throw new InvalidOperationException("WPF overlay check failed", failure);
        }
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
