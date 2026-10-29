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
    public void The_move_tip_shows_once_over_the_game_and_waits_out_layout_mode()
    {
        Loc.Instance.SetLanguage(Loc.English);
        foreach (var make in new Func<(Func<AppSettings, bool> Offer, Func<(bool Shown, string Title, string Detail)> Toast, Action<bool> Layout)>[]
                 {
                     () => { var vm = new WpfOverlay(); return (vm.OfferMoveTip, () => (vm.ShowToast, vm.ToastTitle, vm.ToastDetail), v => vm.LayoutEditing = v); },
                     () => { var vm = new AvaOverlay(); return (vm.OfferMoveTip, () => (vm.ShowToast, vm.ToastTitle, vm.ToastDetail), v => vm.LayoutEditing = v); },
                 })
        {
            var settings = new AppSettings();
            var first = make();
            first.Layout(true);
            Assert.False(first.Offer(settings));
            Assert.False(settings.OverlayMoveTipShown);
            Assert.False(first.Toast().Shown);

            first.Layout(false);
            Assert.True(first.Offer(settings));
            Assert.True(settings.OverlayMoveTipShown);
            Assert.Equal((true, Loc.T("Overlay.Move.TipTitle"), Loc.T("Overlay.Move.Tip")), first.Toast());
            Assert.Equal("Hold Ctrl over a panel to move and resize it", first.Toast().Detail);

            // The flag is in the settings: a new overlay (theme change, next start) does not show it again.
            var next = make();
            Assert.False(next.Offer(settings));
            Assert.False(next.Toast().Shown);
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

        // A drag that loses the mouse (the panel hid, the player switched windows) ends and saves where the panel is.
        PanelPlacement? saved = null;
        deck.PlacementChanged += (_, placement) => saved = placement;
        deck.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Assert.True(deck.IsDragging);
        deck.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Assert.False(deck.IsDragging);
        Assert.NotNull(saved);
        Assert.Equal(1, saved!.Scale);

        // Released Ctrl: back to letting clicks through, no marker.
        deck.IsDragArmed = false;
        Assert.False(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Collapsed, Part(deck, "PART_Grip").Visibility);

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
