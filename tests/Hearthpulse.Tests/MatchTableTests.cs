using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>
/// Plan R2 (<c>docs/match-replay.md</c>): the Constructed table of a step marks the attacker and its target (or a play's
/// target), the health each card gained or lost on the step, the hand and Secret counts; the game's event feed lists every
/// action by turn. On the synthetic game of <see cref="MatchTimelineTests.Events"/>.
/// </summary>
public sealed class MatchTableTests : IDisposable
{
    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-match-table-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    private MatchPage Page(params string[] events)
    {
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), events.Length > 0 ? events : MatchTimelineTests.Events));
        var page = MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null);
        Assert.Null(page.Missing);
        return page;
    }

    private static int FrameOf(MatchPage page, ReplayActionKind kind) => page.Replay!.Frames.First(f => f.Action.Kind == kind).Index;

    [Fact]
    public void An_attack_marks_its_attacker_and_target_and_the_health_lost()
    {
        // The opponent's 4/5 (id 4) attacks our 2/3 (id 3), which takes 4.
        var page = Page();
        var step = page.Step(FrameOf(page, ReplayActionKind.Attack), ReplayPlayer.Local)!;

        Assert.True(step.IsAttack);
        Assert.Equal((4, 3), (step.ActorRef, step.TargetRef));
        var attacker = Assert.Single(step.Top.Board);
        Assert.True(attacker.Acting);
        Assert.False(attacker.Targeted);
        Assert.Equal(4, attacker.Ref);
        Assert.Equal(0, attacker.Change);
        var target = step.Bottom.Board.Single(c => c.Ref == 3);
        Assert.True(target.Targeted);
        Assert.Equal(-4, target.Change);
    }

    [Fact]
    public void A_hero_power_aimed_at_a_minion_marks_it_as_a_target_not_an_attack()
    {
        var page = Page();
        var step = page.Step(FrameOf(page, ReplayActionKind.HeroPower), ReplayPlayer.Local)!;

        Assert.False(step.IsAttack);
        Assert.Equal((6, 4), (step.ActorRef, step.TargetRef));
        Assert.True(step.Top.Board.Single(c => c.Ref == 4).Targeted);
    }

    [Fact]
    public void A_heros_health_change_and_the_hand_and_secret_counts_show_on_its_side()
    {
        string[] events =
        [
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "E 10 L HAND 1 CS2_029 SPELL", "E 11 L HAND 2 CS2_120 MINION",
            "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
            "T 2 O 30", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -", "A 4 1", "V 1 DAMAGE 4",
            "R LOST",
        ];
        var page = Page(events);
        var step = page.Step(FrameOf(page, ReplayActionKind.Attack), ReplayPlayer.Local)!;

        Assert.Equal(1, step.TargetRef);
        Assert.True(step.Bottom.HeroTargeted);
        Assert.Equal(-4, step.Bottom.HealthChange);
        Assert.Equal(0, step.Top.HealthChange);
        Assert.Equal(step.Bottom.Hand.Count, step.Bottom.HandCount);
        Assert.Equal(0, step.Bottom.SecretCount);
    }

    /// <summary>An exchange: the minion that died on the step stays where it stood, dying, with its arrow end and badge.</summary>
    [Fact]
    public void A_minion_that_died_on_the_step_stays_as_dying_where_it_stood()
    {
        string[] events =
        [
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
            "B", "E 5 L PLAY 2 CS2_121 MINION ATK=2 HEALTH=2", "P 5 -",
            "T 2 O 30", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -", "A 4 3", "V 4 DAMAGE 2", "E 3 L GRAVEYARD 0 CS2_120 MINION",
            "R LOST",
        ];
        var page = Page(events);
        var step = page.Step(FrameOf(page, ReplayActionKind.Attack), ReplayPlayer.Local)!;

        Assert.Equal([(3, true), (5, false)], step.Bottom.Board.Select(c => (c.Ref, c.Dying)));
        var dead = step.Bottom.Board[0];
        Assert.True(dead.Targeted);
        Assert.Equal(-3, dead.Change);
        Assert.Equal("−3", dead.ChangeText);
        Assert.Equal(-2, step.Top.Board.Single().Change);

        // The next step has it no more.
        var next = page.Step(FrameOf(page, ReplayActionKind.Attack) + 1, ReplayPlayer.Local);
        Assert.DoesNotContain(next?.Bottom.Board ?? [], c => c.Dying);
    }

    /// <summary>
    /// M2 (review): only a minion that died is a ghost. One that left the table otherwise on the step (back to the hand,
    /// shuffled into the deck, transformed into another, taken by the other side) is not, and the arrow never ends on one.
    /// </summary>
    [Theory]
    [InlineData("E 3 L HAND 3 CS2_120 MINION")]
    [InlineData("E 3 L DECK 0 CS2_120 MINION")]
    [InlineData("X 3")]
    [InlineData("E 3 O PLAY 2 CS2_120 MINION ATK=2 HEALTH=3")]
    public void A_minion_that_left_the_table_without_dying_is_no_ghost(string leaves)
    {
        string[] events =
        [
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
            "T 2 O 30", "E 9 O HAND 1 CS2_029 SPELL", "B", "P 9 3", leaves,
            "R LOST",
        ];
        var page = Page(events);
        var play = page.Replay!.Frames.Last(f => f.Action.Kind == ReplayActionKind.Play).Index;
        var step = page.Step(play, ReplayPlayer.Local)!;

        Assert.DoesNotContain(step.Bottom.Board, c => c.Dying);
        Assert.DoesNotContain(step.Top.Board, c => c.Dying);
        Assert.Empty(page.Replay.Frames[play].Action.Deaths);
    }

    /// <summary>A spell aimed at a minion is no longer on the table: its arrow starts at its side's hero.</summary>
    [Fact]
    public void A_spells_arrow_starts_at_its_heros_portrait()
    {
        string[] events =
        [
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "T 2 O 30", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -",
            "T 3 L 60", "E 9 L HAND 1 CS2_029 SPELL", "B", "P 9 4", "V 4 DAMAGE 6",
            "R WON",
        ];
        var page = Page(events);
        var play = page.Replay!.Frames.Last(f => f.Action.Kind == ReplayActionKind.Play).Index;
        var step = page.Step(play, ReplayPlayer.Local)!;

        Assert.False(step.IsAttack);
        Assert.Equal((1, 4), (step.ActorRef, step.TargetRef));
        Assert.True(step.Bottom.HeroActing);
    }

    [Fact]
    public void The_feed_lists_every_action_under_its_turn_and_marks_the_step()
    {
        var page = Page();
        var attack = FrameOf(page, ReplayActionKind.Attack);
        var feed = page.Feed(attack);

        Assert.Equal(page.Turns.Count, feed.Count(r => r.Header));
        Assert.Equal(page.Replay!.Frames.Count, feed.Count(r => !r.Header));
        Assert.Equal(attack, Assert.Single(feed, r => r.Current && !r.Header).Frame);
        // The header of the step's turn is marked too (the feed shows which turn it is in).
        Assert.Single(feed, r => r.Current && r.Header);
        // Each turn's header comes before its actions.
        Assert.True(feed[0].Header);
        Assert.Equal(feed.Where(r => !r.Header).Select(r => r.Frame).Order(), feed.Where(r => !r.Header).Select(r => r.Frame));
    }
}
