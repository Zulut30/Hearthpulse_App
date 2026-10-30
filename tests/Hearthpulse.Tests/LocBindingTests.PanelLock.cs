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
    /// The Avalonia overlay's unlock: the pointer resting on a locked panel shows the "Unlock panels" button, the only
    /// part that takes the mouse; a real click unlocks every panel (frames on the free ones, saved), the pointer alone
    /// then moves a panel, and the Discover row over the game's cards stays the game's. The tag's button locks again.
    /// </summary>
    [Fact]
    public void Avalonia_panels_unlock_from_the_button_on_a_resting_pointer_and_move_without_ctrl() => Headless.Run(PanelLock);

    /// <summary>The overlay's lock button changes the setting; the host asks the main view, and its switch shows it.</summary>
    [Fact]
    public void Avalonia_settings_switch_follows_the_overlay_lock_button() => Headless.Run(() => WithMainView((view, model) =>
    {
        var unlock = view.FindControl<CheckBox>("PanelLockSetting")!;
        Assert.False(unlock.IsChecked);
        model.Settings.OverlayPanelsUnlocked = true;
        view.SyncOverlaySwitch();
        Dispatcher.UIThread.RunJobs();
        Assert.True(unlock.IsChecked);
        Assert.True(model.Settings.OverlayPanelsUnlocked);
    }));

    private static void PanelLock()
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
            var layoutChanges = 0;
            var lockChanges = 0;
            view.LayoutChanged += () => layoutChanges++;
            view.PanelLockChanged += () => lockChanges++;
            window.Content = view;
            window.Show();
            view.Fit(1920, 1080);
            var panels = view.GetLogicalDescendants().OfType<MovablePanel>().ToDictionary(p => p.Id);
            var timer = panels["turn-timer"];
            var choice = panels["choice-row"];
            timer.Shown = true;
            choice.Shown = true;
            // A Discover row holds the offered cards; empty here, so it is given their height.
            choice.MinHeight = 120;
            // Low on the left, clear of the Discover row and with room above for the tag.
            timer.Apply(new PanelPlacement { X = 0.05, Y = 0.6, Scale = 1 }, new Size(1920, 1080));
            Settle();
            var button = Part<Button>(timer, "PART_Lock");
            // The shield takes a drag over the panel's gaps at the overlay's lowest opacity (0.3, the clamp in Fit):
            // its alpha times that stays at least 1 of 255, or the window's pixels would hand the press to the game.
            var shield = (Avalonia.Media.ISolidColorBrush)Part<Border>(timer, "Shield").Background!;
            Assert.True(shield.Color.A * 0.3 >= 1, shield.Color.ToString());
            var clock = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            DateTime At(int milliseconds) => clock.AddMilliseconds(milliseconds);

            // Locked: passing over the panel changes nothing; resting on it shows the button alone, and only the button
            // takes the mouse.
            Assert.False(view.UpdateDragArm(Inside(timer), controlDown: false, At(0)));
            Settle();
            Assert.False(button.IsEffectivelyVisible);
            Assert.False(view.UpdateDragArm(Inside(timer), controlDown: false, At(450)));
            Settle();
            Assert.True(timer.OffersLock);
            Assert.False(timer.IsDragArmed);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(Part<TextBlock>(timer, "UnlockLabel").IsEffectivelyVisible);
            Assert.Equal("Unlock panels", Part<TextBlock>(timer, "UnlockLabel").Text);
            Assert.False(Part<Border>(timer, "Tag").IsEffectivelyVisible);
            Assert.False(Part<Rectangle>(timer, "Frame").IsVisible);
            var lockSpot = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), view)!.Value;
            Assert.True(view.IsInteractiveAt(lockSpot));
            Assert.False(view.IsInteractiveAt(Inside(timer)));
            Assert.False(view.UpdateDragArm(lockSpot, controlDown: false, At(500)));
            Assert.True(timer.OffersLock);

            // A real click on it: every panel unlocks and the setting is saved.
            window.MouseDown(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            window.MouseUp(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            Assert.True(model.PanelsUnlocked);
            Assert.True(view.Settings.OverlayPanelsUnlocked);
            Assert.Equal((1, 1), (lockChanges, layoutChanges));
            Assert.False(timer.IsDragging);
            Assert.False(view.UpdateDragArm(null, controlDown: false, At(533)));
            Settle();
            // A frame on every free panel, none on the Discover row over the game's cards.
            Assert.True(Part<Rectangle>(timer, "Frame").IsVisible);
            Assert.False(Part<Rectangle>(choice, "Frame").IsVisible);

            // Unlocked, the pointer alone arms the panel: it takes the mouse and its tag says how to lock again.
            Assert.True(view.UpdateDragArm(Inside(timer), controlDown: false, At(566)));
            Settle();
            Assert.True(timer.IsDragArmed);
            Assert.True(view.IsInteractiveAt(Inside(timer)));
            Assert.True(Part<Border>(timer, "PART_Grip").IsVisible);
            Assert.True(Part<Border>(timer, "Tag").IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(Part<TextBlock>(timer, "LockLabel").IsEffectivelyVisible);
            Assert.False(Part<TextBlock>(timer, "UnlockLabel").IsEffectivelyVisible);
            Assert.Equal("Lock panels", Part<TextBlock>(timer, "LockLabel").Text);

            // The Discover row stays the game's however long the pointer rests on it; Ctrl still moves it.
            Assert.False(view.UpdateDragArm(Inside(choice), controlDown: false, At(600)));
            Assert.False(view.UpdateDragArm(Inside(choice), controlDown: false, At(2000)));
            Assert.False(choice.IsDragArmed);
            Assert.False(choice.OffersLock);
            Settle();
            Assert.False(view.IsInteractiveAt(Inside(choice)));
            Assert.False(timer.IsDragArmed);
            Assert.True(view.UpdateDragArm(Inside(choice), controlDown: true, At(2033)));
            Assert.True(choice.IsDragArmed);
            Assert.False(choice.OffersLock);

            // Locked again from the tag's button: no frames, the panel lets clicks through.
            Assert.True(view.UpdateDragArm(Inside(timer), controlDown: false, At(2066)));
            Settle();
            window.MouseDown(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            window.MouseUp(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            Assert.False(timer.IsDragging);
            Assert.False(model.PanelsUnlocked);
            Assert.False(view.Settings.OverlayPanelsUnlocked);
            Assert.Equal((2, 2), (lockChanges, layoutChanges));
            Assert.False(view.UpdateDragArm(Inside(timer), controlDown: false, At(2100)));
            Settle();
            Assert.False(timer.IsDragArmed);
            Assert.False(view.IsInteractiveAt(Inside(timer)));
            Assert.False(Part<Rectangle>(timer, "Frame").IsVisible);

            // Layout mode keeps its own tools: no lock button there.
            model.LayoutEditing = true;
            Assert.False(view.UpdateDragArm(Inside(timer), controlDown: false, At(3000)));
            Settle();
            Assert.False(timer.OffersLock);
            Assert.False(button.IsEffectivelyVisible);

            Point Inside(MovablePanel panel) => panel.TranslatePoint(new Point(10, 10), view)!.Value;
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
