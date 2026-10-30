using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking.Counters;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// The overlay's counter chip as built from a counter view (shared presentation, <c>OverlayModels.cs</c>): goal segments
/// or a bar, the tooltip's card lists cut at eight, when it opens, and value equality over the lists, which keeps an
/// unchanged chip from being rebuilt on every snapshot.
/// </summary>
public sealed class CounterChipTests
{
    private static CounterView View(double? progress = null, int? goal = null, IReadOnlyList<string>? details = null) =>
        new("corpses", "Corpses", "3", progress, false, "RLK_123", "Corpses you have", details ?? [], true) { Goal = goal };

    private static List<CounterCard> Cards(int count, int copies = 1) =>
        Enumerable.Range(1, count).Select(i => new CounterCard($"CARD_{i}", $"Card {i}", copies)).ToList();

    [Fact]
    public void A_short_goal_is_drawn_as_segments_with_the_done_ones_first()
    {
        var chip = CounterChip.From(View(progress: 0.5, goal: 4), expanded: false);

        Assert.Equal([true, true, false, false], chip.Segments);
        Assert.True(chip.HasProgress);
        Assert.False(chip.HasBar);
        Assert.Equal(0.5, chip.Progress);
    }

    [Fact]
    public void A_long_goal_is_a_bar_and_a_count_without_progress_is_neither()
    {
        var longGoal = CounterChip.From(View(progress: 0.25, goal: 12), expanded: false);
        Assert.Empty(longGoal.Segments);
        Assert.True(longGoal.HasBar);

        var plain = CounterChip.From(View(), expanded: false);
        Assert.False(plain.HasProgress);
        Assert.False(plain.HasBar);
        Assert.Equal(0, plain.Progress);
        Assert.Empty(plain.Segments);
    }

    [Fact]
    public void Tooltip_lists_stop_at_eight_cards_and_say_how_many_more()
    {
        var view = View() with { Counted = Cards(11, copies: 2), RelatedCards = Cards(9) };

        var chip = CounterChip.From(view, expanded: false);

        Assert.Equal(8, chip.Counted.Count);
        Assert.All(chip.Counted, c => Assert.Equal("×2", c.Count));
        Assert.Equal(Loc.F("Overlay.Counter.More", 3), chip.MoreCounted);
        Assert.Equal(8, chip.Related.Count);
        // A related card's copies only show when there are several.
        Assert.All(chip.Related, c => Assert.Equal(string.Empty, c.Count));
        Assert.Equal(Loc.F("Overlay.Counter.More", 1), chip.MoreRelated);
    }

    [Fact]
    public void Lists_of_eight_or_fewer_have_no_more_line_and_goal_parts_keep_their_state()
    {
        var view = View() with
        {
            Counted = Cards(8),
            Parts = [new("P1", "First", 0, true), new("P2", "Second", 0, false), new("P3", "Third", 0)],
        };

        var chip = CounterChip.From(view, expanded: false);

        Assert.Equal(string.Empty, chip.MoreCounted);
        Assert.Equal([true, false, false], chip.Parts.Select(p => p.Done));
        Assert.All(chip.Parts, p => Assert.True(p.IsPart));
    }

    [Fact]
    public void A_chip_opens_only_when_it_has_something_to_show()
    {
        Assert.False(CounterChip.From(View(), expanded: true).Expanded);
        Assert.True(CounterChip.From(View() with { RelatedCards = Cards(1) }, expanded: true).Expanded);

        var detailed = CounterChip.From(View(details: ["Ghoul", "Abomination"]), expanded: true);
        Assert.True(detailed.Expanded);
        Assert.Equal("Corpses you have" + Environment.NewLine + "Ghoul" + Environment.NewLine + "Abomination", detailed.Tooltip);
        Assert.Equal("Corpses you have", detailed.Description);
        Assert.False(CounterChip.From(View(details: ["Ghoul"]), expanded: false).Expanded);
    }

    [Fact]
    public void Chips_built_twice_from_equal_views_are_equal_and_a_changed_list_is_not()
    {
        var view = View(progress: 0.5, goal: 4, details: ["Ghoul"]) with { Counted = Cards(3), RelatedCards = Cards(2) };

        var first = CounterChip.From(view, expanded: true);
        var again = CounterChip.From(view with { Counted = Cards(3), RelatedCards = Cards(2) }, expanded: true);
        var changed = CounterChip.From(view with { Counted = Cards(4) }, expanded: true);

        Assert.NotSame(first.Counted, again.Counted);
        Assert.Equal(first, again);
        Assert.Equal(first.GetHashCode(), again.GetHashCode());
        Assert.NotEqual(first, changed);
        Assert.NotEqual(first, CounterChip.From(view, expanded: false));
    }
}
