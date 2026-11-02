using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// Plan R1 (<c>docs/match-replay.md</c>, <c>docs/replay-library.md</c>): the player's "Turn" list and mulligan, bookmarks
/// kept by the replay's name, and the library's search and result filter.
/// </summary>
public sealed class MatchNavigationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hp-match-nav-{Guid.NewGuid():N}");
    private readonly ReplayStore _store;

    public MatchNavigationTests() => _store = new ReplayStore(Path.Combine(_folder, "replays"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The history store's pool may still hold its file; the temp folder goes with the temp folder.
        }
    }

    private HistoryStore History()
    {
        Directory.CreateDirectory(_folder);
        return new HistoryStore(Path.Combine(_folder, "history.db"));
    }

    /// <summary>The Constructed game of <see cref="MatchTimelineTests.Events"/>: ten steps over the mulligan and three turns.</summary>
    private (MatchViewModel Model, string Replay) Opened(HistoryStore history)
    {
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events));
        Assert.NotNull(name);
        var model = new MatchViewModel(new MainViewModel(history, new AppSettings()));
        model.Show(MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null));
        return (model, name);
    }

    [Fact]
    public void The_turn_list_starts_with_the_mulligan_and_jumps_to_a_turns_first_step()
    {
        var (model, _) = Opened(History());

        Assert.True(model.HasMulligan);
        Assert.True(model.Jumps[0].Mulligan);
        Assert.Equal(model.Jumps.Select(j => j.FirstFrame).Order(), model.Jumps.Select(j => j.FirstFrame));
        Assert.Equal(0, model.JumpIndex);

        model.JumpIndex = model.Jumps.Count - 1;
        Assert.Equal(model.Jumps[^1].FirstFrame, model.Frame);
        Assert.Equal(model.Jumps.Count - 1, model.JumpIndex);

        model.GoMulligan.Execute(null);
        Assert.Equal(model.Jumps[0].FirstFrame, model.Frame);
    }

    [Fact]
    public async Task Bookmarks_are_kept_by_the_replays_name_and_walked_in_order()
    {
        var history = History();
        var (model, name) = Opened(history);
        Assert.False(model.CanBookmark);

        model.SetReplay(name, null, history);
        await model.BookmarkWork;
        Assert.True(model.CanBookmark);
        model.Frame = 7;
        model.ToggleBookmark.Execute(null);
        await model.BookmarkWork;
        model.Frame = 3;
        var marks = model.BookmarkMarks;
        model.ToggleBookmark.Execute(null);
        await model.BookmarkWork;
        Assert.True(model.IsBookmarked);
        Assert.Equal([3, 7], history.Bookmarks(name).Select(b => b.Frame));
        Assert.Equal([3, 7], model.BookmarkRows.Select(r => r.Frame));
        Assert.Equal([3, 7], model.BookmarkMarks.Select(r => r.Frame));
        Assert.NotSame(marks, model.BookmarkMarks);

        // The timeline's marks change only when a bookmark does, not at every step.
        marks = model.BookmarkMarks;
        model.Frame = 5;
        model.Frame = 7;
        Assert.Same(marks, model.BookmarkMarks);

        model.Frame = 0;
        model.NextBookmark.Execute(null);
        Assert.Equal(3, model.Frame);
        model.NextBookmark.Execute(null);
        Assert.Equal(7, model.Frame);
        model.NextBookmark.Execute(null);
        Assert.Equal(7, model.Frame);
        model.PreviousBookmark.Execute(null);
        Assert.Equal(3, model.Frame);

        // Opened again (another session): the bookmarks come back; one taken off is gone for good.
        var (again, _) = Opened(history);
        again.SetReplay(name, null, history);
        await again.BookmarkWork;
        Assert.Equal([3, 7], again.BookmarkRows.Select(r => r.Frame));
        again.Frame = 7;
        again.ToggleBookmark.Execute(null);
        await again.BookmarkWork;
        Assert.False(again.IsBookmarked);
        Assert.Equal([3], history.Bookmarks(name).Select(b => b.Frame));

        // Another game's page lets them go.
        again.Clear();
        Assert.Empty(again.BookmarkRows);
        Assert.False(again.CanBookmark);
    }

    /// <summary>
    /// M2 (R1 review): a replay written again with another step count (a newer recorder, a reconnect, an import) moves its
    /// bookmarks to their place in their stretch; one whose place is gone is dropped, in the history too.
    /// </summary>
    [Fact]
    public async Task Bookmarks_find_their_place_again_in_a_replay_with_other_steps()
    {
        var history = History();
        var (model, name) = Opened(history);
        var starts = model.Jumps.Select(j => j.Start).ToList();
        Assert.True(starts.Count >= 3);
        // Set on a recording of 120 steps: the second step of the third stretch, and a stretch this file has not.
        history.SetBookmark(name, new ReplayBookmark(Frame: 77, FrameCount: 120, Stretch: 2, Offset: 1), on: true);
        history.SetBookmark(name, new ReplayBookmark(Frame: 99, FrameCount: 120, Stretch: 40, Offset: 0), on: true);

        model.SetReplay(name, null, history);
        await model.BookmarkWork;

        var moved = starts[2] + 1;
        Assert.Equal([moved], model.BookmarkRows.Select(r => r.Frame));
        var kept = Assert.Single(history.Bookmarks(name));
        Assert.Equal(new ReplayBookmark(moved, model.FrameMax + 1, 2, 1), kept);
    }

    [Fact]
    public void A_bookmark_resolves_by_its_step_count_else_its_stretch()
    {
        int[] starts = [0, 3, 6];
        var bookmark = ReplayBookmark.At(4, 10, starts);
        Assert.Equal(new ReplayBookmark(4, 10, 1, 1), bookmark);
        Assert.Equal(4, bookmark.Resolve(10, starts));
        Assert.Equal(6, bookmark.Resolve(12, [0, 5, 9]));
        // The stretch is shorter now, or gone.
        Assert.Null(bookmark.Resolve(12, [0, 5, 6]));
        Assert.Null(bookmark.Resolve(12, [0]));
    }

    /// <summary>m1 (R1 review): bookmarks go with the replay's last file.</summary>
    [Fact]
    public void Bookmarks_go_when_their_replay_has_no_file_left()
    {
        var history = History();
        history.SetBookmark("gone.hpr", new ReplayBookmark(1, 10, 0, 1), on: true);
        history.SetBookmark("kept.hpr", new ReplayBookmark(1, 10, 0, 1), on: true);
        Assert.Equal(["gone.hpr", "kept.hpr"], history.BookmarkedReplays().Order());

        history.RemoveBookmarks("gone.hpr");
        Assert.Equal(["kept.hpr"], history.BookmarkedReplays());
    }

    /// <summary>
    /// M1 (R1 review): the library shows another tab or filter, so the game has no card there: the header's card is made
    /// for it and still knows its file and its star.
    /// </summary>
    [Fact]
    public async Task The_header_has_its_star_and_share_when_the_library_shows_another_filter()
    {
        var history = History();
        var files = new ReplayLibrary(_store, Path.Combine(_folder, "saved"), Path.Combine(_folder, "imported"));
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events))!;
        var game = MatchTimelineTests.Game(name);
        history.Add(game);
        Assert.True(files.Star(ReplayShelf.Recent, name));
        var main = new MainViewModel(history, new AppSettings()) { LibraryFiles = files };
        await main.RefreshAsync();
        main.SetHistoryFilter.Execute("Battlegrounds");
        await main.LibraryIdleAsync();
        Assert.DoesNotContain(main.Library.AllCards, c => c.HistoryKey == game.Key);

        Assert.True(await main.OpenMatchPageAsync(game.Key));

        var header = main.MatchPage.HeaderCard!;
        Assert.True(header.HasReplay);
        Assert.True(header.Starred);
        Assert.True(main.MatchPage.ShowHeaderActions);
    }

    [Fact]
    public void The_library_search_matches_card_texts_and_the_result_filter()
    {
        LibraryCard Card(string title, string opponent, string archetype, string result) =>
            new(ReplayShelf.Recent, "x.hpr", null, battlegrounds: false) { Title = title, Opponent = opponent, OpponentArchetype = archetype, ResultKey = result, Caption = "Ranked · Standard" };

        var priest = Card("Priest", "Mage", "Spell Mage", "Win");
        var hunter = Card("Hunter", "Rogue", "Cycle Rogue", "Loss");

        Assert.True(ReplayLibraryViewModel.Matches(priest, "", "All"));
        Assert.True(ReplayLibraryViewModel.Matches(priest, "  spell ", "All"));
        Assert.True(ReplayLibraryViewModel.Matches(hunter, "ROGUE", "All"));
        Assert.True(ReplayLibraryViewModel.Matches(hunter, "standard", "All"));
        Assert.False(ReplayLibraryViewModel.Matches(priest, "rogue", "All"));
        Assert.True(ReplayLibraryViewModel.Matches(priest, "", "Win"));
        Assert.False(ReplayLibraryViewModel.Matches(hunter, "", "Win"));
        Assert.False(ReplayLibraryViewModel.Matches(hunter, "hunter", "Win"));
    }

    [Fact]
    public void The_library_shows_the_cards_the_search_lets_through_and_says_when_none()
    {
        var history = History();
        var main = new MainViewModel(history, new AppSettings());
        var library = main.Library;
        LibraryCard Card(string title, string result) => new(ReplayShelf.Recent, title + ".hpr", null, battlegrounds: false) { Title = title, ResultKey = result };
        library.SetCards([Card("Priest", "Win"), Card("Hunter", "Loss"), Card("Mage", "Win")]);

        library.Search = "mage";
        Assert.Equal(["Mage"], library.Cards.Select(c => c.Title));
        Assert.True(library.IsSearching);

        library.Search = string.Empty;
        library.SetResultFilter.Execute("Loss");
        Assert.Equal(["Hunter"], library.Cards.Select(c => c.Title));

        library.Search = "priest";
        Assert.Empty(library.Cards);
        Assert.True(library.IsEmpty);
        Assert.True(library.IsSearchEmpty);
        Assert.Equal(Loc.F("Library.Search.Empty", "priest"), library.EmptyText);
        Assert.Equal("\U000F0980", library.EmptyGlyph);

        // Only the result filter lets nothing through: its own line, the same "Clear search".
        library.Search = "  ";
        library.SetCards([Card("Priest", "Win"), Card("Mage", "Win")]);
        Assert.True(library.IsSearchEmpty);
        Assert.Equal(Loc.T("Library.Search.EmptyResult"), library.EmptyText);

        library.ClearSearch.Execute(null);
        Assert.Equal(2, library.Cards.Count);
        Assert.False(library.IsSearching);
        Assert.False(library.IsSearchEmpty);

        // An empty tab keeps its own text, without the button, whatever the search.
        library.Search = "mage";
        library.SetCards([]);
        Assert.True(library.IsEmpty);
        Assert.False(library.IsSearchEmpty);
        Assert.NotEqual(Loc.F("Library.Search.Empty", "mage"), library.EmptyText);
    }

    [Fact]
    public void A_card_chosen_for_the_player_clears_a_search_that_would_hide_it()
    {
        var main = new MainViewModel(History(), new AppSettings());
        var library = main.Library;
        LibraryCard Card(string title, string result) => new(ReplayShelf.Recent, title + ".hpr", null, battlegrounds: false) { Title = title, ResultKey = result };
        var priest = Card("Priest", "Win");

        library.Search = "hunter";
        library.SetResultFilter.Execute("Loss");
        library.SetCards([priest, Card("Hunter", "Loss")], select: priest);

        Assert.Equal(string.Empty, library.Search);
        Assert.True(library.ResultAll);
        Assert.Same(priest, library.SelectedCard);
        Assert.Equal(2, library.Cards.Count);
    }

    [Fact]
    public void The_turn_list_names_the_timelines_stretches_and_its_mulligan_goes_to_the_choice_step()
    {
        var (model, _) = Opened(History());

        Assert.Equal(
            [Loc.T("Match.Step.Mulligan"), Loc.F("Match.Step.Turn", "1", Loc.T("Match.You")), Loc.F("Match.Step.Turn", "1", Loc.T("Match.Opponent")),
                Loc.F("Match.Step.Turn", "2", Loc.T("Match.You"))],
            model.Jumps.Select(j => j.Text));
        Assert.Equal([MatchTimelineLook.Neutral, MatchTimelineLook.Mine, MatchTimelineLook.Theirs, MatchTimelineLook.Mine], model.Jumps.Select(j => j.Look));
        Assert.Equal(model.TimelineSegments.Select(s => s.Tooltip), model.Jumps.Select(j => j.Tooltip));

        // The game's start (step 1) shows as the mulligan; the entry and M go to the step where the offer shows.
        Assert.Equal(0, model.Frame);
        Assert.Equal(0, model.JumpIndex);
        Assert.Equal(model.FrameOf(ReplayActionKind.Mulligan), model.Jumps[0].FirstFrame);
        Assert.Equal(1, model.Jumps[0].FirstFrame);
        model.GoMulligan.Execute(null);
        Assert.Equal(1, model.Frame);
        Assert.Equal(0, model.JumpIndex);
        Assert.EndsWith(Loc.T("Match.Player.Help"), model.HelpText);
    }

    [Fact]
    public void G_opens_the_turn_list_which_follows_the_steps_again_when_it_closes()
    {
        var (model, _) = Opened(History());
        var raised = 0;
        model.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(MatchViewModel.JumpIndex) ? 1 : 0;

        model.OpenTurns.Execute(null);
        Assert.True(model.TurnsOpen);
        model.Frame = 5;
        Assert.Equal(0, raised);

        model.TurnsOpen = false;
        Assert.Equal(1, raised);
        Assert.Equal(2, model.JumpIndex);
    }

    [Fact]
    public void Without_a_mulligan_step_M_does_nothing_and_the_keys_help_leaves_it_out()
    {
        var model = new MatchViewModel(new MainViewModel(History(), new AppSettings()));

        Assert.False(model.HasMulligan);
        model.GoMulligan.Execute(null);
        Assert.Equal(0, model.Frame);
        model.OpenTurns.Execute(null);
        Assert.False(model.TurnsOpen);
        Assert.EndsWith(Loc.T("Match.Player.HelpNoMulligan"), model.HelpText);
        Assert.NotEqual(Loc.T("Match.Player.Help"), Loc.T("Match.Player.HelpNoMulligan"));
    }

    [Fact]
    public async Task Bookmarks_are_named_by_step_and_turn_and_the_menu_goes_to_them()
    {
        var history = History();
        var (model, name) = Opened(history);
        model.SetReplay(name, null, history);
        await model.BookmarkWork;
        Assert.Equal(Loc.T("Match.Player.BookmarksEmpty"), model.BookmarksTip);
        Assert.Equal(string.Empty, model.BookmarkCountText);
        Assert.False(model.HasPreviousBookmark);
        Assert.False(model.HasNextBookmark);

        foreach (var frame in new[] { 0, 1, 5 })
        {
            model.Frame = frame;
            model.ToggleBookmark.Execute(null);
            await model.BookmarkWork;
        }

        // The start, the mulligan, a step of turn 1.
        Assert.Equal(
            [Loc.F("Match.Player.BookmarkStep", "1"), Loc.F("Match.Player.BookmarkMulligan", "2"), Loc.F("Match.Player.BookmarkTurn", "6", 1)],
            model.BookmarkRows.Select(r => r.Text));
        Assert.Equal("3", model.BookmarkCountText);
        Assert.Equal(Loc.P("Match.Player.BookmarksTip", 3), model.BookmarksTip);

        // The step on screen is the menu's current one and the toggle is on.
        Assert.Equal([false, false, true], model.BookmarkRows.Select(r => r.Current));
        Assert.True(model.IsBookmarked);
        Assert.Equal("\U000F00C0", model.BookmarkGlyph);
        Assert.Equal(Loc.T("Match.Player.BookmarkRemove"), model.BookmarkTip);
        Assert.True(model.HasPreviousBookmark);
        Assert.False(model.HasNextBookmark);

        model.GoBookmark.Execute(model.BookmarkRows[1]);
        Assert.Equal(1, model.Frame);
        Assert.Equal([false, true, false], model.BookmarkRows.Select(r => r.Current));

        model.Frame = 3;
        Assert.False(model.IsBookmarked);
        Assert.Equal("\U000F00C3", model.BookmarkGlyph);
        Assert.Equal(Loc.T("Match.Player.BookmarkAdd"), model.BookmarkTip);
        // A step without a bookmark is not a pick of the menu.
        model.GoBookmark.Execute(4);
        Assert.Equal(3, model.Frame);
    }

    [Fact]
    public async Task A_bookmark_that_cannot_be_written_stays_as_it_was_and_the_header_says_so()
    {
        var history = History();
        var (model, name) = Opened(history);
        model.SetReplay(name, null, history);
        await model.BookmarkWork;
        Assert.Empty(history.Bookmarks(name));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var file = Path.Combine(_folder, "history.db");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            model.ToggleBookmark.Execute(null);
            await model.BookmarkWork;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Assert.False(model.IsBookmarked);
        Assert.Empty(model.BookmarkRows);
        Assert.Equal(Loc.T("Match.Player.BookmarkFailed"), model.HeaderStatus);
        Assert.Equal("Loss", model.HeaderStatusKey);

        // The next action takes the line away.
        model.ToggleBookmark.Execute(null);
        await model.BookmarkWork;
        Assert.True(model.IsBookmarked);
        Assert.Equal(string.Empty, model.HeaderStatus);
    }

    [Fact]
    public void The_headers_star_acts_on_the_games_library_card_and_reports_in_the_header()
    {
        var history = History();
        var files = new ReplayLibrary(_store, Path.Combine(_folder, "saved"), Path.Combine(_folder, "imported"));
        var main = new MainViewModel(history, new AppSettings()) { LibraryFiles = files };
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events));
        Assert.NotNull(name);
        var model = new MatchViewModel(main);
        model.Show(MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null));
        Assert.False(model.ShowHeaderActions);

        var card = new LibraryCard(ReplayShelf.Recent, name, MatchTimelineTests.Game(name).Key, battlegrounds: false) { HasReplay = true };
        model.SetReplay(name, card, history);
        Assert.True(model.ShowHeaderActions);

        // The star: a copy in Saved, the same state on the card; off again while the game is in Recent, without a question.
        model.ToggleHeaderStar.Execute(null);
        Assert.True(card.Starred);
        Assert.True(files.Saved.Contains(name));
        Assert.Equal(string.Empty, model.HeaderStatus);
        model.ToggleHeaderStar.Execute(null);
        Assert.False(card.Starred);
        Assert.False(model.HeaderConfirming);

        // The only copy left: the question in the header; "Delete" takes the file, and with it the star and Share.
        model.ToggleHeaderStar.Execute(null);
        Assert.True(_store.Delete(name));
        model.ToggleHeaderStar.Execute(null);
        Assert.True(model.HeaderConfirming);
        Assert.Equal(Loc.T("Library.Star.ConfirmLast"), card.ConfirmText);
        model.ConfirmHeader.Execute(null);
        Assert.False(model.HeaderConfirming);
        Assert.False(model.ShowHeaderActions);
        Assert.False(files.Saved.Contains(name));

        // A copy that cannot be made: the library's message shows in the header; another tab takes it away, there too.
        var missing = new LibraryCard(ReplayShelf.Recent, "missing.hpr", null, battlegrounds: false) { HasReplay = true };
        model.SetReplay("missing.hpr", missing, history);
        model.ToggleHeaderStar.Execute(null);
        Assert.Equal(Loc.T("Library.Star.Failed"), model.HeaderStatus);
        Assert.Equal("Loss", model.HeaderStatusKey);
        model.SetTab.Execute("Decks");
        Assert.Equal(string.Empty, model.HeaderStatus);
        Assert.Equal(string.Empty, main.Library.Status);
    }
}
