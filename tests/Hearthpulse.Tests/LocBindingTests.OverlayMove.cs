using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
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
    /// The Avalonia overlay during a game: Ctrl over a panel arms it (frame, move hint, corner marker) and the overlay
    /// takes the mouse there; let go, the panel lets clicks through again. Dragging the marker resizes the panel in wheel
    /// steps and saves it; the overlay keeps the mouse for the whole drag, and a drag that lost the pointer or its
    /// release ends.
    /// </summary>
    [Fact]
    public void Avalonia_panel_arms_with_ctrl_resizes_by_its_corner_and_ends_a_drag_that_lost_the_pointer() => Headless.Run(OverlayMoveResize);

    private static void OverlayMoveResize()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1920, Height = 1080 };
        // The overlay's UI font is Inter, which the app registers at startup and the headless platform does not have.
        window.Resources["UiFont"] = Avalonia.Media.FontFamily.Default;
        try
        {
            var model = new OverlayViewModel();
            var view = new OverlayView { DataContext = model };
            var layoutChanges = 0;
            view.LayoutChanged += () => layoutChanges++;
            window.Content = view;
            window.Show();
            view.Fit(1920, 1080);
            // The turn timer has no buttons of its own: outside Ctrl every click over it is the game's.
            var timer = view.GetLogicalDescendants().OfType<MovablePanel>().Single(p => p.Id == "turn-timer");
            timer.Shown = true;
            Settle();
            Assert.True(timer.Bounds.Width > 0);

            Assert.False(view.UpdateDragArm(Inside(), controlDown: false));
            Assert.False(timer.IsDragArmed);
            Assert.False(view.IsInteractiveAt(Inside()));
            Assert.False(view.UpdateDragArm(new Point(5, 5), controlDown: true));

            Arm();
            Assert.True(timer.IsDragArmed);
            Assert.True(view.IsInteractiveAt(Inside()));
            Assert.True(Part<Rectangle>(timer, "Frame").IsVisible);
            var hint = Part<TextBlock>(timer, "ArmedHint");
            Assert.True(hint.IsVisible);
            Assert.Equal("Drag to move · corner or wheel to resize · right-click to reset", hint.Text);
            var grip = Part<Border>(timer, "PART_Grip");
            Assert.True(grip.IsVisible);
            var area = timer.ArmedArea();
            Assert.True(area.Right > timer.Bounds.Width + 3 && area.Bottom > timer.Bounds.Height + 3, area.ToString());

            // Ctrl let go: no frame, no marker, clicks go to the game.
            Assert.False(view.UpdateDragArm(Inside(), controlDown: false));
            Settle();
            Assert.False(timer.IsDragArmed);
            Assert.False(grip.IsVisible);
            Assert.False(view.IsInteractiveAt(Inside()));

            // Drag the corner a fifth of the way out along the diagonal: 120 %, saved like a wheel step, the top-left
            // corner where it was.
            Arm();
            var origin = timer.TranslatePoint(default, view);
            var size = timer.Bounds.Size;
            var corner = timer.TranslatePoint(new Point(size.Width - 3, size.Height - 3), window)!.Value;
            window.MouseDown(corner, MouseButton.Left);
            Assert.True(timer.IsDragging);
            // Ctrl let go and the pointer off the panel and the game: the overlay keeps the mouse until the button is up.
            Assert.True(view.UpdateDragArm(null, controlDown: false));
            Assert.True(timer.IsDragArmed);
            window.MouseMove(corner + new Point(size.Width * 0.2, size.Height * 0.2));
            Settle();
            Assert.Equal(PanelSnap.ScaleText(1.2), timer.ScaleText);
            window.MouseUp(corner + new Point(size.Width * 0.2, size.Height * 0.2), MouseButton.Left);
            Settle();
            Assert.False(timer.IsDragging);
            Assert.Equal(1.2, view.Settings.Panels["turn-timer"].Scale, 3);
            Assert.Equal(origin, timer.TranslatePoint(default, view));
            Assert.Equal(1, layoutChanges);
            Assert.False(view.UpdateDragArm(null, controlDown: false));
            Assert.False(timer.IsDragArmed);

            // A drag that loses the pointer (the player switched windows) ends where the panel is and is saved.
            Arm();
            var body = timer.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(body, MouseButton.Left);
            Assert.True(timer.IsDragging);
            timer.RaiseEvent(new PointerCaptureLostEventArgs(timer, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true)));
            Assert.False(timer.IsDragging);
            Assert.Equal(2, layoutChanges);
            Assert.False(view.UpdateDragArm(null, controlDown: false));
            Assert.False(timer.IsDragArmed);
            window.MouseUp(body, MouseButton.Left);

            // A drag whose release never reached the panel: once the host reads both buttons up during a game, it ends,
            // so the overlay does not keep the mouse over the whole game. Not while a button is down, nor in layout mode.
            Arm();
            body = timer.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(body, MouseButton.Left);
            Assert.True(timer.IsDragging);
            view.EndReleasedDrags((true, false));
            view.EndReleasedDrags((false, true));
            Assert.True(timer.IsDragging);
            model.LayoutEditing = true;
            view.EndReleasedDrags((false, false));
            Assert.True(timer.IsDragging);
            model.LayoutEditing = false;
            view.EndReleasedDrags((false, false));
            Assert.False(timer.IsDragging);
            Assert.Equal(3, layoutChanges);
            Assert.False(view.UpdateDragArm(null, controlDown: false));
            Assert.False(timer.IsDragArmed);
            window.MouseUp(body, MouseButton.Left);
            Assert.Equal(3, layoutChanges);

            // The one-time tip: shown and saved the first time the overlay appears over the game, never again.
            view.OfferMoveTip();
            Assert.True(model.ShowToast);
            Assert.Equal("Hold Ctrl over a panel to move and resize it", model.ToastDetail);
            Assert.True(view.Settings.OverlayMoveTipShown);
            Assert.Equal(4, layoutChanges);
            view.OfferMoveTip();
            Assert.Equal(4, layoutChanges);

            // Layout mode: the marker on every shown panel, none on the placeholders.
            model.LayoutEditing = true;
            Settle();
            Assert.True(grip.IsVisible);
            var placeholder = view.GetLogicalDescendants().OfType<MovablePanel>().Single(p => p.Id == "opponent");
            Assert.False(Part<Border>(placeholder, "PART_Grip").IsVisible);

            // A point just inside the panel's top-left corner, where it is now (the content's width can change).
            Point Inside() => timer.TranslatePoint(new Point(10, 10), view)!.Value;

            void Arm()
            {
                Assert.True(view.UpdateDragArm(Inside(), controlDown: true));
                Settle();
            }
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }

        // Hit tests read the last rendered frame: render after the layout pass.
        void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        static T Part<T>(Control panel, string name) where T : Control =>
            Assert.Single(panel.GetVisualDescendants().OfType<T>(), c => c.Name == name);
    }
}
