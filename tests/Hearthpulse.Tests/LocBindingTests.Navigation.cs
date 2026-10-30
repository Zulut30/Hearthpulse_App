using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Avalonia sidebar: a radio raises IsCheckedChanged for the newly checked button while the old one is still
/// checked, so the page shown is the sender's, not the first checked one (WPF unchecks the group first).
/// </summary>
public sealed partial class LocBindingTests
{
    /// <summary>A game opened from the Overview's recent games moves the nav to History and its page stays open.</summary>
    [Fact]
    public void A_game_opened_off_the_history_page_stays_open() => Headless.Run(() =>
    {
        var history = new HistoryStore(Path.Combine(AppPaths.Root, $"hp-nav-match-{Guid.NewGuid():N}.db"));
        var game = MatchTimelineTests.Game(null);
        Assert.True(history.Add(game));
        WithMainView((view, model) =>
        {
            view.ShowPage("Overview");
            Dispatcher.UIThread.RunJobs();

            var open = model.OpenMatchPageAsync(game.Key);
            Assert.True(model.MatchOpen);
            Assert.Equal("History", view.CurrentPage);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!open.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.True(open.IsCompletedSuccessfully && open.Result);
            Assert.True(model.MatchOpen);
            Assert.True(view.FindControl<ScrollViewer>("PageMatch")!.IsVisible);

            // Leaving History lets the match go.
            view.ShowPage("Overview");
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.MatchOpen);
        }, model: new MainViewModel(history, new AppSettings()));
    });

    /// <summary>Standard → Wild → Standard: each step shows only the format it goes to.</summary>
    [Fact]
    public void Standard_to_wild_shows_only_the_format_it_goes_to() => Headless.Run(() => WithMainView((view, model) =>
    {
        view.ShowPage(ConstructedPage.Standard);
        Dispatcher.UIThread.RunJobs();
        var formats = new List<string>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ConstructedFormat))
            {
                formats.Add(model.ConstructedFormat);
            }
        };

        view.ShowPage(ConstructedPage.Wild);
        Assert.Equal(ConstructedPage.Wild, view.CurrentPage);
        view.ShowPage(ConstructedPage.Standard);
        Assert.Equal(ConstructedPage.Standard, view.CurrentPage);
        Assert.Equal([ConstructedPage.FormatOf(ConstructedPage.Wild), ConstructedPage.FormatOf(ConstructedPage.Standard)], formats);
    }));
}
