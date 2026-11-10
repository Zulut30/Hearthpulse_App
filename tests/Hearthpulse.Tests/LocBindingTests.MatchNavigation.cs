using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The Avalonia match page's jumps (plan R1, <c>docs/match-replay.md</c>): the "Turn" list over the timeline, the keys
/// G, M, B, [ and ] by their physical place, the bookmark marks on the timeline.
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Match_player_turn_list_bookmarks_and_their_keys() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-nav-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder);
        try
        {
            model.SetReplay("bookmarks.hpr", null, new HistoryStore(Path.Combine(folder, "history.db")));
            Dispatcher.UIThread.RunJobs();

            // The "Turn" list holds the timeline's stretches and shows the one on screen.
            var turns = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TurnBox");
            Assert.Equal(model.Jumps.Count, turns.ItemCount);
            Assert.Equal(0, turns.SelectedIndex);

            // M: the mulligan's choice step; B there, then ] from the start and [ back.
            Assert.True(Letter(view, PhysicalKey.M).Handled);
            Assert.Equal(1, model.Frame);
            Letter(view, PhysicalKey.B);
            Assert.True(model.IsBookmarked);
            model.Frame = 5;
            Letter(view, PhysicalKey.B);
            model.Frame = 0;
            Letter(view, PhysicalKey.BracketRight);
            Assert.Equal(1, model.Frame);
            Letter(view, PhysicalKey.BracketRight);
            Assert.Equal(5, model.Frame);
            Letter(view, PhysicalKey.BracketLeft);
            Assert.Equal(1, model.Frame);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(model.JumpIndex, turns.SelectedIndex);

            // A mark for each bookmark on the timeline.
            var timeline = view.GetVisualDescendants().OfType<MatchTimeline>().Single();
            Assert.Equal(model.TimelineSegments.Count + model.BookmarkRows.Count + 1 + 2 + 2, timeline.Children.Count);

            // G opens the list; while it is open the player's keys are the list's.
            Assert.True(Letter(view, PhysicalKey.G).Handled);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.TurnsOpen);
            Assert.True(turns.IsDropDownOpen);
            Assert.False(Press(view, Key.Right).Handled);
            Assert.False(Letter(view, PhysicalKey.B).Handled);
            Assert.Equal(1, model.Frame);
            turns.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.TurnsOpen);
            Assert.True(Press(view, Key.Right).Handled);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    /// <summary>A letter by its place on the keyboard, whatever the layout makes of it (Key.None: not a Latin letter).</summary>
    private static KeyEventArgs Letter(Interactive target, PhysicalKey key)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.None, PhysicalKey = key, Source = target };
        target.RaiseEvent(e);
        return e;
    }
}
