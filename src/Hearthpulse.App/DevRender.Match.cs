using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Hearthpulse.App;

/// <summary>
/// Render-check scenes of the match page: the newest Constructed game with a replay (a step two thirds in, from both
/// sides, its decks and analysis) and the newest Battlegrounds game with one.
/// </summary>
public partial class App
{
    private async Task RenderMatchPageAsync(string folder)
    {
        var games = _history.Recent(400).Where(g => g.Replay is not null).ToList();
        var constructed = games.FirstOrDefault(g => !g.IsBattlegrounds);
        if (constructed is not null && await _mainViewModel.OpenMatchPageAsync(constructed.Key))
        {
            var page = _mainViewModel.MatchPage;
            page.Frame = page.FrameMax * 2 / 3;
            await SaveMatchAsync(folder, "main-match");
            page.BottomOpponent = true;
            await SaveMatchAsync(folder, "main-match-opponent-side", full: false);
            page.BottomLocal = true;
            page.ShowDecks = true;
            await SaveMatchAsync(folder, "main-match-decks");
            page.ShowAnalysis = true;
            await SaveMatchAsync(folder, "main-match-analysis");
            page.ShowReplay = true;

            // The step's choice row: both mulligans (the opponent's as the log revealed it), then a Discover.
            foreach (var (kind, name) in new[] { (Core.Replay.ReplayActionKind.Mulligan, "main-match-mulligan"), (Core.Replay.ReplayActionKind.Choice, "main-match-choice") })
            {
                if (page.FrameOf(kind) is >= 0 and var frame)
                {
                    page.Frame = frame;
                    await SaveMatchAsync(folder, name);
                }
            }

            await RenderMatchNavigationAsync(folder, page);
        }


        var battlegrounds = games.FirstOrDefault(g => g.IsBattlegrounds);
        if (battlegrounds is not null && await _mainViewModel.OpenMatchPageAsync(battlegrounds.Key))
        {
            await SaveMatchAsync(folder, "main-match-bg");
        }

        _mainViewModel.MatchPage.Back.Execute(null);
    }

    /// <summary>
    /// Plan R1: bookmarks on three steps (written to the history, so only on a copy of the data: HEARTHPULSE_DATA, as
    /// render.py sets it; taken off again), the "Bookmarks" menu and the "Turn" list open, and the page at the
    /// narrowest window.
    /// </summary>
    private async Task RenderMatchNavigationAsync(string folder, Main.Match.MatchViewModel page)
    {
        var sample = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEARTHPULSE_DATA")) && page.CanBookmark;
        if (sample)
        {
            foreach (var frame in new[] { page.FrameMax / 4, page.FrameMax / 2, page.FrameMax * 2 / 3 })
            {
                page.Frame = frame;
                if (!page.IsBookmarked)
                {
                    page.ToggleBookmark.Execute(null);
                }
            }

            await page.BookmarkWork;
            await SaveMatchAsync(folder, "main-match-bookmarks", full: false);
            if (Named<Button>(_main, "BookmarksButton") is { ContextMenu: { } menu } button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                menu.UpdateLayout();
                SaveCard(menu, Path.Combine(folder, "main-match-bookmarks-menu.png"));
                menu.IsOpen = false;
            }
        }
        else
        {
            Console.WriteLine("main-match-bookmarks: skipped, HEARTHPULSE_DATA is not set (bookmarks are written to the history)");
        }

        if (Named<ComboBox>(_main, "TurnBox") is { } turns)
        {
            turns.IsDropDownOpen = true;
            _main.UpdateLayout();
            if (turns.Template.FindName("PART_Popup", turns) is Popup { Child: FrameworkElement list })
            {
                list.UpdateLayout();
                SaveCard(list, Path.Combine(folder, "main-match-turns-open.png"));
            }

            turns.IsDropDownOpen = false;
        }

        var width = _main.Width;
        _main.Width = 1040;
        await SaveMatchAsync(folder, "main-match-1040", full: false);
        _main.Width = width;

        if (sample)
        {
            foreach (var frame in page.BookmarkRows.Select(r => r.Frame).ToList())
            {
                page.Frame = frame;
                page.ToggleBookmark.Execute(null);
            }

            await page.BookmarkWork;
        }
    }

    /// <summary>The first element of a type and name under <paramref name="root"/> (a view's own name scope included).</summary>
    private static T? Named<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T { } element && element.Name == name)
            {
                return element;
            }

            if (Named<T>(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private async Task SaveMatchAsync(string folder, string name, bool full = true)
    {
        _main.ShowPage("History");
        await Task.Delay(2500);
        _main.UpdateLayout();
        Save(_main, Path.Combine(folder, name + ".png"));
        if (full && _main.FindName("PageMatch") is ScrollViewer { Content: FrameworkElement content })
        {
            SaveFull(content, Path.Combine(folder, name + "-full.png"));
        }
    }
}
