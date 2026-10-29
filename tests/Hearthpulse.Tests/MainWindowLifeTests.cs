using Hearthpulse.Core;

namespace Hearthpulse.Tests;

/// <summary>
/// When both hosts let the main window go and build it again: after a minute hidden in the tray, never during a game,
/// and a window the player closed for good is forgotten (opening it again used to show a closed window and throw).
/// </summary>
public sealed class MainWindowLifeTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Window(string page)
    {
        public string Page { get; set; } = page;
    }

    private sealed record Place(string Page);

    private static MainWindowLife<Window, Place> Life() => new(w => new Place(w.Page));

    [Fact]
    public void The_window_is_built_once_and_again_on_the_last_place_after_it_is_let_go()
    {
        var life = Life();
        var builds = new List<Place?>();
        Window Build(Place? place)
        {
            builds.Add(place);
            return new Window(place?.Page ?? "Overview");
        }

        var first = life.Open(Build);
        Assert.Same(first, life.Open(Build));
        first.Page = "Battlegrounds";
        Assert.Same(first, life.Release());
        Assert.Null(life.Window);

        var second = life.Open(Build);
        Assert.NotSame(first, second);
        Assert.Equal([null, new Place("Battlegrounds")], builds);
        Assert.Equal("Battlegrounds", second.Page);
    }

    [Fact]
    public void A_hidden_window_is_let_go_after_a_minute_but_not_during_a_game()
    {
        var life = Life();
        var window = life.Open(_ => new Window("Overview"));
        life.Hidden(window, T0);
        Assert.True(life.Waiting);
        Assert.False(life.ReleaseDue(T0 + MemoryTrimmer.HiddenWindowRelease - TimeSpan.FromSeconds(1), inGame: false));
        Assert.True(life.ReleaseDue(T0 + MemoryTrimmer.HiddenWindowRelease, inGame: false));

        // Put away for a game: the pages go once the match is over, not in the middle of it.
        Assert.False(life.ReleaseDue(T0 + TimeSpan.FromMinutes(15), inGame: true));
        Assert.True(life.ReleaseDue(T0 + TimeSpan.FromMinutes(15), inGame: false));

        // Shown again within the minute: nothing to let go; hidden again, the minute starts over.
        life.Shown(window);
        Assert.False(life.Waiting);
        Assert.False(life.ReleaseDue(T0 + TimeSpan.FromMinutes(20), inGame: false));
        life.Hidden(window, T0 + TimeSpan.FromMinutes(20));
        Assert.False(life.ReleaseDue(T0 + TimeSpan.FromMinutes(20.5), inGame: false));
        Assert.True(life.ReleaseDue(T0 + TimeSpan.FromMinutes(21), inGame: false));
    }

    [Fact]
    public void A_window_closed_for_good_is_forgotten_and_the_next_one_opens_where_it_was()
    {
        var life = Life();
        var window = life.Open(_ => new Window("Overview"));
        window.Page = "History";
        life.Hidden(window, T0);
        life.Closing(window);
        life.Closed(window);
        Assert.Null(life.Window);
        Assert.False(life.Waiting);
        Assert.False(life.ReleaseDue(T0 + TimeSpan.FromHours(1), inGame: false));
        Assert.Equal(new Place("History"), life.Place);

        var next = life.Open(place => new Window(place!.Page));
        Assert.NotSame(window, next);
        Assert.Equal("History", next.Page);
    }

    [Fact]
    public void Events_of_a_window_already_let_go_change_nothing()
    {
        var life = Life();
        var old = life.Open(_ => new Window("Overview"));
        life.Release();
        var current = life.Open(_ => new Window("Arena"));

        life.Hidden(old, T0);
        Assert.False(life.Waiting);
        life.Closing(old);
        life.Closed(old);
        Assert.Same(current, life.Window);
        Assert.Equal(new Place("Overview"), life.Place);
    }
}
