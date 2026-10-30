using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Panels unlock for free moving during a game. Covered here: the arming rule both overlays share (Ctrl, unlocked,
/// panels on the game's cards, the button's wait, one log line per change); the setting in both view-model flavours;
/// the WPF template and tick (the unlock button on a resting pointer, a frame on every free panel, the lock button).
/// </summary>
public sealed class OverlayPanelLockTests
{
    private delegate (bool Armed, bool Offered, string? Note) Arm(string? over, bool onGameCards, bool controlDown, bool unlocked, DateTime now);

    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int milliseconds) => T0.AddMilliseconds(milliseconds);

    [Fact]
    public void Ctrl_or_unlocked_panels_arm_the_panel_under_the_pointer_and_a_resting_pointer_offers_the_unlock_button()
    {
        foreach (var arm in new Arm[] { new Hearthpulse.App.Overlay.PanelArming().Update, new Hearthpulse.UI.Overlay.PanelArming().Update })
        {
            // Locked, no Ctrl: the panel lets clicks through; its button shows once the pointer has rested on it.
            Assert.Equal((false, false, null), arm("deck", false, false, false, At(0)));
            Assert.Equal((false, false, null), arm("deck", false, false, false, At(300)));
            Assert.Equal((false, true, null), arm("deck", false, false, false, At(450)));
            // Another panel starts the wait again.
            Assert.Equal((false, false, null), arm("opponent", false, false, false, At(500)));

            // Ctrl arms at once, with the button, and says so in the log once.
            Assert.Equal((true, true, "Overlay panel opponent armed (Ctrl)"), arm("opponent", false, true, false, At(533)));
            Assert.Equal((true, true, null), arm("opponent", false, true, false, At(566)));
            Assert.Equal((false, false, "Overlay panel opponent released"), arm(null, false, true, false, At(600)));
            Assert.Equal((false, false, null), arm(null, false, false, false, At(633)));
            // Ctrl over empty space: one line, so the log tells "Ctrl read, no panel there" from "Ctrl never read".
            Assert.Equal((false, false, "Overlay: Ctrl held with no panel under the pointer"), arm(null, false, true, false, At(666)));
            Assert.Equal((false, false, null), arm(null, false, true, false, At(700)));

            // Unlocked: the pointer alone arms a panel, with the lock button.
            Assert.Equal((true, true, "Overlay panel deck armed (panels unlocked)"), arm("deck", false, false, true, At(733)));
            // A row on the game's cards neither arms nor offers the button however long the pointer rests on it:
            // Discover and the tavern stay clickable. Ctrl still moves it.
            Assert.Equal((false, false, "Overlay panel deck released"), arm("choice-row", true, false, true, At(766)));
            Assert.Equal((false, false, null), arm("choice-row", true, false, true, At(2000)));
            Assert.Equal((true, false, "Overlay panel choice-row armed (Ctrl)"), arm("choice-row", true, true, true, At(2033)));

            // Locked again with the pointer still on the panel: it lets go at once, and its button waits again.
            Assert.Equal((true, true, "Overlay panel deck armed (panels unlocked)"), arm("deck", false, false, true, At(2066)));
            Assert.Equal((false, false, "Overlay panel deck released"), arm("deck", false, false, false, At(2100)));
        }
    }

    [Fact]
    public void Drags_and_placements_make_one_readable_log_line()
    {
        Assert.Equal("Overlay panel deck drag started (move)", Hearthpulse.App.Overlay.PanelArming.DragStarted("deck", false));
        Assert.Equal("Overlay panel deck drag started (corner, resize)", Hearthpulse.UI.Overlay.PanelArming.DragStarted("deck", true));
        Assert.Equal("Overlay panel deck placed at 0.3, 0.125, size 1.2",
            Hearthpulse.App.Overlay.PanelArming.Placed("deck", new PanelPlacement { X = 0.3, Y = 0.125, Scale = 1.2 }));
        Assert.Equal("Overlay panel deck reset to its default spot", Hearthpulse.UI.Overlay.PanelArming.Placed("deck", null));
    }

    [Fact]
    public void The_unlock_survives_a_restart_and_both_view_model_flavours_follow_the_setting()
    {
        foreach (var make in new Func<(Func<bool> Unlocked, Action<AppSettings> Toggle, Action<AppSettings> Update)>[]
                 {
                     () => { var vm = new WpfOverlay(); return (() => vm.PanelsUnlocked, vm.TogglePanelLock, s => vm.Update(LiveSnapshot.Empty, CardDb.Empty, s, null, null)); },
                     () => { var vm = new AvaOverlay(); return (() => vm.PanelsUnlocked, vm.TogglePanelLock, s => vm.Update(LiveSnapshot.Empty, CardDb.Empty, s, null, null)); },
                 })
        {
            var settings = new AppSettings();
            var overlay = make();
            Assert.False(overlay.Unlocked());

            // The overlay's button flips the setting and the panels at once.
            overlay.Toggle(settings);
            Assert.True(settings.OverlayPanelsUnlocked);
            Assert.True(overlay.Unlocked());

            // The next start (or a theme change) reads it back from the settings.
            var next = make();
            next.Update(AppSettings.FromJson(settings.ToJson()));
            Assert.True(next.Unlocked());

            // The Settings switch changes the setting; the overlay follows on the update that comes after it.
            settings.OverlayPanelsUnlocked = false;
            next.Update(settings);
            Assert.False(next.Unlocked());
            overlay.Toggle(settings);
            overlay.Toggle(settings);
            Assert.False(settings.OverlayPanelsUnlocked);
        }

        Assert.False(AppSettings.FromJson("{}").OverlayPanelsUnlocked);
    }

    [Fact]
    public void Wpf_panels_offer_the_unlock_button_then_move_without_ctrl_except_on_the_game_cards() => Sta(WpfPanels);

    private static void WpfPanels()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var model = new WpfOverlay();
        var settings = new AppSettings();
        var window = new OverlayWindow(model, settings);
        var lockChanges = 0;
        var layoutChanges = 0;
        window.PanelLockChanged += () => lockChanges++;
        window.LayoutChanged += () => layoutChanges++;
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1920, 1080));
        root.Arrange(new Rect(0, 0, 1920, 1080));
        // The window is never shown: its Loaded handler collects the panels the tick looks at.
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Idle();
        var panels = Descendants<MovablePanel>(root).ToDictionary(p => p.Id);
        var deck = panels["deck"];
        var choice = panels["choice-row"];
        deck.Shown = true;
        choice.Shown = true;
        // A Discover row holds the offered cards; empty here, so it is given their height.
        choice.MinHeight = 120;
        // Low on the left, clear of the Discover row and with room above for the tag.
        deck.Apply(new PanelPlacement { X = 0.05, Y = 0.6, Scale = 1 }, new Size(1920, 1080));
        root.UpdateLayout();
        FrameworkElement Part(MovablePanel panel, string name) => (FrameworkElement)panel.Template.FindName(name, panel);
        Point Inside(MovablePanel panel) => panel.TranslatePoint(new Point(10, 10), root);
        var button = (ButtonBase)Part(deck, "PART_Lock");

        // The shield takes a drag over the panel's gaps. The overlay is a per-pixel layered window, so its pixels decide
        // where the mouse goes, and the overlay's opacity (down to 0.3, the clamp in Tick) multiplies the shield's alpha:
        // at 1/255 it rounded to 0 from 50 % on and the press went to the game.
        var shield = (SolidColorBrush)((Border)Part(deck, "Shield")).Background;
        Assert.True(shield.Color.A * 0.3 >= 1, shield.Color.ToString());

        // Locked: the pointer passing over the deck changes nothing; resting on it shows the unlock button alone,
        // and only the button takes the mouse.
        Assert.False(window.UpdateDragArm(Inside(deck), controlDown: false, At(0)));
        root.UpdateLayout();
        Assert.False(deck.OffersLock);
        Assert.Equal(Visibility.Collapsed, button.Visibility);
        Assert.False(window.UpdateDragArm(Inside(deck), controlDown: false, At(450)));
        root.UpdateLayout();
        Assert.True(deck.OffersLock);
        Assert.False(deck.IsDragArmed);
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(Visibility.Visible, Part(deck, "UnlockLabel").Visibility);
        Assert.Equal("Unlock panels", ((TextBlock)Part(deck, "UnlockLabel")).Text);
        Assert.Equal(Visibility.Collapsed, Part(deck, "Tag").Visibility);
        Assert.Equal(Visibility.Collapsed, Part(deck, "Frame").Visibility);
        Assert.True(OverlayHit.GetInteractive(button));
        Assert.False(OverlayHit.GetInteractive(deck));
        // The button sits above the panel; the pointer on its way there keeps it up.
        var lockSpot = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), root);
        var lockCorner = button.TranslatePoint(default, root);
        Assert.True(lockSpot.Y < deck.TranslatePoint(default, root).Y, lockSpot.ToString());
        Assert.False(window.UpdateDragArm(lockSpot, controlDown: false, At(500)));
        Assert.True(deck.OffersLock);

        // Pressed: every panel unlocks and the setting is saved.
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(model.PanelsUnlocked);
        Assert.True(settings.OverlayPanelsUnlocked);
        Assert.Equal((1, 1), (lockChanges, layoutChanges));
        Assert.False(window.UpdateDragArm(null, controlDown: false, At(533)));
        root.UpdateLayout();
        // A frame on every free panel, none on the Discover row over the game's cards.
        Assert.Equal(Visibility.Visible, Part(deck, "Frame").Visibility);
        Assert.Equal(Visibility.Collapsed, Part(choice, "Frame").Visibility);

        // Unlocked, the pointer alone arms the deck: it takes the mouse and its tag says how to lock again.
        Assert.True(window.UpdateDragArm(Inside(deck), controlDown: false, At(566)));
        root.UpdateLayout();
        Assert.True(deck.IsDragArmed);
        Assert.True(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Visible, Part(deck, "PART_Grip").Visibility);
        Assert.Equal(Visibility.Visible, Part(deck, "Tag").Visibility);
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(Visibility.Visible, Part(deck, "LockLabel").Visibility);
        Assert.Equal(Visibility.Collapsed, Part(deck, "UnlockLabel").Visibility);
        Assert.Equal("Lock panels", ((TextBlock)Part(deck, "LockLabel")).Text);
        // The button stays where it was pressed: the tag and tools appear beside it, so a second click is not a drag.
        var moved = button.TranslatePoint(default, root) - lockCorner;
        Assert.True(Math.Abs(moved.X) < 0.5 && Math.Abs(moved.Y) < 0.5, moved.ToString());

        // The Discover row stays the game's however long the pointer rests on it; Ctrl still moves it.
        Assert.False(window.UpdateDragArm(Inside(choice), controlDown: false, At(600)));
        Assert.False(window.UpdateDragArm(Inside(choice), controlDown: false, At(2000)));
        Assert.False(choice.IsDragArmed);
        Assert.False(choice.OffersLock);
        Assert.False(OverlayHit.GetInteractive(choice));
        Assert.False(deck.IsDragArmed);
        Assert.True(window.UpdateDragArm(Inside(choice), controlDown: true, At(2033)));
        Assert.True(choice.IsDragArmed);
        Assert.False(choice.OffersLock);

        // Locked again from the deck's tag: no frames, the deck lets clicks through.
        Assert.True(window.UpdateDragArm(Inside(deck), controlDown: false, At(2066)));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(model.PanelsUnlocked);
        Assert.False(settings.OverlayPanelsUnlocked);
        Assert.Equal((2, 2), (lockChanges, layoutChanges));
        Assert.False(window.UpdateDragArm(Inside(deck), controlDown: false, At(2100)));
        root.UpdateLayout();
        Assert.False(deck.IsDragArmed);
        Assert.False(OverlayHit.GetInteractive(deck));
        Assert.Equal(Visibility.Collapsed, Part(deck, "Frame").Visibility);

        // Layout mode keeps its own tools: no lock button there.
        model.LayoutEditing = true;
        Assert.False(window.UpdateDragArm(Inside(deck), controlDown: false, At(3000)));
        root.UpdateLayout();
        Assert.False(deck.OffersLock);
        Assert.Equal(Visibility.Collapsed, button.Visibility);
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
