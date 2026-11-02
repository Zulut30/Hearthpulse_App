using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Desktop;

/// <summary>
/// Render-check scenes of the match page: the newest Constructed game with a replay (a step two thirds in, from both
/// sides, its decks and analysis, and at phone width) and the newest Battlegrounds game with one.
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderMatchPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        var games = host.History.Recent(400).Where(g => g.Replay is not null).ToList();
        var page = host.Main.MatchPage;
        if (games.FirstOrDefault(g => !g.IsBattlegrounds) is { } constructed && await host.Main.OpenMatchPageAsync(constructed.Key))
        {
            view.ShowPage("History");
            page.Frame = page.FrameMax * 2 / 3;
            await SaveMatch(main, folder, "main-match");
            page.BottomOpponent = true;
            await Save(main, Path.Combine(folder, "main-match-opponent-side.png"));
            page.BottomLocal = true;
            page.ShowDecks = true;
            await SaveMatch(main, folder, "main-match-decks");
            page.ShowAnalysis = true;
            await SaveMatch(main, folder, "main-match-analysis");
            page.ShowReplay = true;

            // The step's choice row: both mulligans (the opponent's as the log revealed it), then a Discover.
            foreach (var (kind, name) in new[] { (Core.Replay.ReplayActionKind.Mulligan, "main-match-mulligan"), (Core.Replay.ReplayActionKind.Choice, "main-match-choice") })
            {
                if (page.FrameOf(kind) is >= 0 and var frame)
                {
                    page.Frame = frame;
                    await Save(main, Path.Combine(folder, name + ".png"));
                }
            }

            await RenderMatchTableAsync(main, view, page, folder);
            page.Frame = page.FrameMax * 2 / 3;
            await RenderMatchNavigationAsync(main, view, page, folder);

            // Phone width: the turn's actions go under the table.
            main.Width = 420;
            main.Height = 2600;
            await Save(main, Path.Combine(folder, "main-match-compact.png"));
            main.Width = 1260;
            main.Height = 840;
        }

        if (games.FirstOrDefault(g => g.IsBattlegrounds) is { } battlegrounds && await host.Main.OpenMatchPageAsync(battlegrounds.Key))
        {
            view.ShowPage("History");
            await SaveMatch(main, folder, "main-match-bg");
        }

        page.Back.Execute(null);
    }

    /// <summary>
    /// Plan R2: the table's marks on the game's first attack (the arrow, the attacker and its target, the change badges),
    /// an exchange (a minion dying on the step, faded where it stood), a play aimed at a card (the dashed arrow), each as
    /// the page and the table card, the event feed's card at the attack, and the attack at the narrowest window and at
    /// phone width (the feed under the table).
    /// </summary>
    private static async Task RenderMatchTableAsync(Window main, MainView view, UI.Main.Match.MatchViewModel page, string folder)
    {
        static bool Dying(Core.Replay.MatchStepView s) => s.Top.Board.Concat(s.Bottom.Board).Any(c => c.Dying);
        Border? Named(string name) => view.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        var scenes = new (string Name, Func<Core.Replay.MatchStepView, bool> Match)[]
        {
            ("main-match-attack", s => s is { IsAttack: true, ActorRef: not 0, TargetRef: not 0 } && !Dying(s)),
            ("main-match-exchange", s => s.IsAttack && Dying(s)),
            ("main-match-target", s => s is { IsAttack: false, ActorRef: not 0, TargetRef: not 0 }),
        };
        foreach (var (name, match) in scenes)
        {
            if (page.FrameWhere(match) is not (>= 0 and var frame))
            {
                Console.WriteLine($"{name}: skipped, the game has no such step");
                continue;
            }

            page.Frame = frame;
            await Save(main, Path.Combine(folder, name + ".png"));
            // The crops from the tall frame: the table and the feed are below the fold at 840.
            main.Height = 2400;
            await Save(main, Path.Combine(folder, name + "-full.png"));
            if (Named("TableCard") is { } table)
            {
                SaveCrop(main, table, Path.Combine(folder, name + "-table.png"));
            }

            if (name == "main-match-attack" && Named("FeedCard") is { } feed)
            {
                SaveCrop(main, feed, Path.Combine(folder, "main-match-feed.png"));
            }

            main.Height = 840;
            if (name == "main-match-attack")
            {
                main.Width = 1040;
                await SaveMatch(main, folder, "main-match-attack-1040");
                main.Width = 420;
                main.Height = 2600;
                await Save(main, Path.Combine(folder, "main-match-attack-compact.png"));
                main.Width = 1260;
                main.Height = 840;
            }
        }
    }

    /// <summary>
    /// Plan R1: bookmarks on three steps (written to the history, so only on a copy of the data: HEARTHPULSE_DATA, as
    /// render.py sets it), the "Turn" list open, and the page at the narrowest window. The "Bookmarks" menu is a flyout
    /// whose popup the headless platform does not hand out: WPF renders it.
    /// </summary>
    private static async Task RenderMatchNavigationAsync(Window main, MainView view, UI.Main.Match.MatchViewModel page, string folder)
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

            await Save(main, Path.Combine(folder, "main-match-bookmarks.png"));
        }
        else
        {
            Console.WriteLine("main-match-bookmarks: skipped, HEARTHPULSE_DATA is not set (bookmarks are written to the history)");
        }

        if (view.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "TurnBox") is { } turns)
        {
            turns.IsDropDownOpen = true;
            await Settle();
            SavePopup(main, turns.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child, Path.Combine(folder, "main-match-turns-open.png"));
            turns.IsDropDownOpen = false;
        }

        main.Width = 1040;
        await Save(main, Path.Combine(folder, "main-match-1040.png"));
        main.Width = 1260;

        if (sample)
        {
            foreach (var frame in page.BookmarkRows.Select(r => r.Frame).ToList())
            {
                page.Frame = frame;
                page.ToggleBookmark.Execute(null);
            }

            page.Frame = page.FrameMax * 2 / 3;
        }
    }

    private static async Task SaveMatch(Window main, string folder, string name)
    {
        await Save(main, Path.Combine(folder, name + ".png"));
        main.Height = 2400;
        await Save(main, Path.Combine(folder, name + "-full.png"));
        main.Height = 840;
    }
}
