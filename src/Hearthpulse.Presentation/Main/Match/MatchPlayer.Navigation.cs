#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.ComponentModel;
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>
/// A stretch of the game to jump to from the "Turn" list: the mulligan, a turn, the hero choice, a tavern, a combat or
/// the end - the timeline's stretches one to one. <see cref="FirstFrame"/> is where choosing it goes (the mulligan: its
/// choice step), <see cref="Text"/> the entry ("Turn 7 · You"), <see cref="Tooltip"/> the stretch's full caption and
/// <see cref="Look"/> its marker colour (<see cref="MatchTimelineLayout.MarkerBrush"/>).
/// </summary>
public sealed record MatchJumpRow(int FirstFrame, string Text, bool Mulligan, string Tooltip = "", MatchTimelineLook Look = MatchTimelineLook.Neutral)
{
    /// <summary>The stretch's first step: the list's entry on screen is the stretch holding the step.</summary>
    internal int Start { get; init; } = FirstFrame;

    // The marker's colour for the Avalonia classes (WPF reads Look).
    public bool MarkMine => Look == MatchTimelineLook.Mine;

    public bool MarkTheirs => Look == MatchTimelineLook.Theirs;

    public bool MarkWon => Look == MatchTimelineLook.Won;

    public bool MarkLost => Look == MatchTimelineLook.Lost;
}

/// <summary>A bookmarked step: "Step 34 · turn 7"; <see cref="Current"/> is the step on screen.</summary>
public sealed record MatchBookmarkRow(int Frame, string Text, bool Current = false);

/// <summary>
/// The player's jumps (<c>docs/match-replay.md</c>, plan R1): the "Turn" list (G), the mulligan (M), bookmarks on steps
/// (B, [ and ]), and the header's star and Share, which act on the game's library card, with the header's status line.
/// </summary>
public sealed partial class MatchViewModel
{
    // MDI glyphs.
    private const string IconBookmark = "\U000F00C0";
    private const string IconBookmarkOutline = "\U000F00C3";
    private const string IconFailed = "\U000F05D6";

    private IReadOnlyList<MatchJumpRow> _jumps = [];
    private readonly SortedSet<int> _bookmarks = [];
    private IReadOnlyList<MatchBookmarkRow> _bookmarkRows = [];
    private IReadOnlyList<MatchBookmarkRow> _bookmarkMarks = [];
    private int _bookmarkLoad;
    private HistoryStore? _bookmarkStore;
    private string? _replayName;
    private LibraryCard? _headerCard;
    private bool _turnsOpen;
    private int _bookmarkCurrent = -1;
    private string _headerStatus = string.Empty;
    private string _headerStatusKey = string.Empty;
    private string _headerStatusGlyph = string.Empty;
    private bool _headerStatusFolder;

    // The header's star and Share report through the library's status line: while one of them is the last action, the
    // header copies it.
    private bool _mirrorLibraryStatus;
    private bool _libraryWatched;
    private RelayCommand? _goMulligan;
    private RelayCommand? _openTurns;
    private RelayCommand? _toggleBookmark;
    private RelayCommand? _previousBookmark;
    private RelayCommand? _nextBookmark;
    private RelayCommand? _goBookmark;
    private RelayCommand? _toggleHeaderStar;
    private RelayCommand? _shareHeader;
    private RelayCommand? _openHeaderStatusFolder;
    private RelayCommand? _confirmHeader;
    private RelayCommand? _cancelHeader;

    /// <summary>The "Turn" list: the timeline's stretches by their first step (Constructed: the mulligan first).</summary>
    public IReadOnlyList<MatchJumpRow> Jumps { get => _jumps; private set => Set(ref _jumps, value); }

    /// <summary>
    /// The list's entry of the step on screen (the stretch holding it, as on the timeline); choosing one goes to its
    /// <see cref="MatchJumpRow.FirstFrame"/>. It does not follow the steps while the list is open.
    /// </summary>
    public int JumpIndex
    {
        get
        {
            var index = -1;
            for (var i = 0; i < _jumps.Count; i++)
            {
                if (_jumps[i].Start <= _frame && (index < 0 || _jumps[i].Start >= _jumps[index].Start))
                {
                    index = i;
                }
            }

            return index;
        }
        set
        {
            if (value >= 0 && value < _jumps.Count && value != JumpIndex)
            {
                Frame = _jumps[value].FirstFrame;
            }
        }
    }

    /// <summary>The "Turn" list is open (bound two-way to the drop-down): the player's keys are the list's meanwhile.</summary>
    public bool TurnsOpen
    {
        get => _turnsOpen;
        set
        {
            if (Set(ref _turnsOpen, value) && !value)
            {
                Raise(nameof(JumpIndex));
            }
        }
    }

    /// <summary>G: opens the "Turn" list.</summary>
    public RelayCommand OpenTurns => _openTurns ??= new RelayCommand(() =>
    {
        if (_jumps.Count > 0)
        {
            TurnsOpen = true;
        }
    });

    /// <summary>The replay has a mulligan step (Constructed).</summary>
    public bool HasMulligan => _jumps.Any(j => j.Mulligan);

    /// <summary>M: the mulligan's choice step; nothing without one.</summary>
    public RelayCommand GoMulligan => _goMulligan ??= new RelayCommand(() =>
    {
        if (_jumps.FirstOrDefault(j => j.Mulligan) is { } mulligan)
        {
            Frame = mulligan.FirstFrame;
        }
    });

    /// <summary>Bookmarks can be kept: the replay has a file (they are kept by its name).</summary>
    public bool CanBookmark => _replayName is not null && _bookmarkStore is not null && FrameMax >= 0;

    /// <summary>The step on screen is bookmarked.</summary>
    public bool IsBookmarked => _bookmarks.Contains(_frame);

    public string BookmarkTip => Loc.T(IsBookmarked ? "Match.Player.BookmarkRemove" : "Match.Player.BookmarkAdd");

    /// <summary>MDI bookmark (on) or bookmark-outline.</summary>
    public string BookmarkGlyph => IsBookmarked ? IconBookmark : IconBookmarkOutline;

    /// <summary>The bookmarked steps for the "Bookmarks" menu, in step order.</summary>
    public IReadOnlyList<MatchBookmarkRow> BookmarkRows { get => _bookmarkRows; private set => Set(ref _bookmarkRows, value); }

    public bool HasBookmarks => _bookmarkRows.Count > 0;

    /// <summary>The number on the "Bookmarks" button; empty without bookmarks.</summary>
    public string BookmarkCountText => _bookmarks.Count > 0 ? _bookmarks.Count.ToString(CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>The "Bookmarks" button's tooltip and name: how many, or how to add one.</summary>
    public string BookmarksTip => _bookmarks.Count > 0 ? Loc.P("Match.Player.BookmarksTip", _bookmarks.Count) : Loc.T("Match.Player.BookmarksEmpty");

    /// <summary>A bookmark before the step on screen ([ and ‹ go there).</summary>
    public bool HasPreviousBookmark => _bookmarks.Count > 0 && _bookmarks.Min < _frame;

    /// <summary>A bookmark after the step on screen (] and › go there).</summary>
    public bool HasNextBookmark => _bookmarks.Count > 0 && _bookmarks.Max > _frame;

    /// <summary>Where the bookmarks sit on the timeline, 0–1 (the middle of their step, as the event ticks).</summary>
    public IReadOnlyList<MatchBookmarkRow> BookmarkMarks { get => _bookmarkMarks; private set => Set(ref _bookmarkMarks, value); }

    /// <summary>
    /// The latest reading or writing of bookmarks, done off the UI thread (tests wait for it). A newer game's page drops
    /// what an older one was still doing.
    /// </summary>
    internal Task BookmarkWork { get; private set; } = Task.CompletedTask;

    /// <summary>B: a bookmark on the step on screen, or off it. A failed write leaves it as it was and says so in the header.</summary>
    public RelayCommand ToggleBookmark => _toggleBookmark ??= new RelayCommand(() =>
    {
        if (!CanBookmark)
        {
            return;
        }

        ClearHeaderStatus();
        var on = !_bookmarks.Contains(_frame);
        var bookmark = ReplayBookmark.At(_frame, FrameMax + 1, StretchStarts());
        // Shown at once; written off the UI thread, and taken back if the write fails.
        Mark(_frame, on);
        BookmarkWork = SaveBookmarkAsync(_bookmarkLoad, _bookmarkStore!, _replayName!, bookmark, on);
    });

    /// <summary>[: the bookmark before the step on screen.</summary>
    public RelayCommand PreviousBookmark => _previousBookmark ??= new RelayCommand(() =>
    {
        if (_bookmarks.GetViewBetween(int.MinValue, _frame - 1) is { Count: > 0 } before)
        {
            Frame = before.Max;
        }
    });

    /// <summary>]: the bookmark after the step on screen.</summary>
    public RelayCommand NextBookmark => _nextBookmark ??= new RelayCommand(() =>
    {
        if (_bookmarks.GetViewBetween(_frame + 1, int.MaxValue) is { Count: > 0 } after)
        {
            Frame = after.Min;
        }
    });

    /// <summary>A pick in the "Bookmarks" menu (a row or a step number): goes to its step.</summary>
    public RelayCommand GoBookmark => _goBookmark ??= new RelayCommand(p =>
    {
        if ((p is MatchBookmarkRow row ? row.Frame : p as int?) is { } frame && _bookmarks.Contains(frame))
        {
            Frame = frame;
        }
    });

    /// <summary>The game's library card (its file): the header's star and Share act on it; null without a file.</summary>
    public LibraryCard? HeaderCard
    {
        get => _headerCard;
        private set
        {
            var old = _headerCard;
            if (Set(ref _headerCard, value))
            {
                if (old is not null)
                {
                    old.PropertyChanged -= OnHeaderCardChanged;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnHeaderCardChanged;
                }

                Raise(nameof(ShowHeaderActions));
                Raise(nameof(HeaderConfirming));
            }
        }
    }

    /// <summary>The header asks before the star takes away the replay's only copy (the card's question, in the status slot).</summary>
    public bool HeaderConfirming => _headerCard is { Confirming: true };

    /// <summary>"Delete" of the header's question: the copy goes, and with the file the star and Share.</summary>
    public RelayCommand ConfirmHeader => _confirmHeader ??= new RelayCommand(() => _owner.Library.Confirm.Execute(_headerCard));

    public RelayCommand CancelHeader => _cancelHeader ??= new RelayCommand(() => _owner.Library.Cancel.Execute(_headerCard));

    /// <summary>The header's star and Share show: the game's replay file is there.</summary>
    public bool ShowHeaderActions => _headerCard is { HasReplay: true };

    public RelayCommand ToggleHeaderStar => _toggleHeaderStar ??= new RelayCommand(() =>
    {
        if (_headerCard is { } card)
        {
            ClearHeaderStatus();
            MirrorLibraryStatus();
            _owner.Library.ToggleStar.Execute(card);
        }
    });

    public RelayCommand ShareHeader => _shareHeader ??= new RelayCommand(() =>
    {
        if (_headerCard is { } card)
        {
            ClearHeaderStatus();
            MirrorLibraryStatus();
            _owner.Library.Share.Execute(card);
        }
    });

    /// <summary>The header's status line under the subtitle: the last star, Share or bookmark result (empty: none).</summary>
    public string HeaderStatus { get => _headerStatus; private set => Set(ref _headerStatus, value); }

    /// <summary>"Win" or "Loss" for the glyph's colour.</summary>
    public string HeaderStatusKey { get => _headerStatusKey; private set => Set(ref _headerStatusKey, value); }

    public string HeaderStatusGlyph { get => _headerStatusGlyph; private set => Set(ref _headerStatusGlyph, value); }

    /// <summary>The Share copy could not go to the clipboard as a file: "Open the folder" shows (Avalonia).</summary>
    public bool HasHeaderStatusFolder { get => _headerStatusFolder; private set => Set(ref _headerStatusFolder, value); }

    public RelayCommand OpenHeaderStatusFolder => _openHeaderStatusFolder ??= new RelayCommand(() => _owner.Library.OpenStatusFolder.Execute(null));

    /// <summary>
    /// The game's replay file and its library card, set by the main window's model when it opens the game: bookmarks
    /// are read and kept by the file's name.
    /// </summary>
    internal void SetReplay(string? replayName, LibraryCard? card, HistoryStore? store)
    {
        ClearHeaderStatus();
        if (_headerCard is { Confirming: true } asked && asked != card)
        {
            // The question asked in the header goes with the page.
            asked.ConfirmText = string.Empty;
            asked.PendingConfirm = null;
        }

        _replayName = string.IsNullOrEmpty(replayName) ? null : replayName;
        _bookmarkStore = store;
        HeaderCard = card;
        _bookmarks.Clear();
        var load = ++_bookmarkLoad;
        BookmarkWork = _replayName is { } name && store is not null
            ? LoadBookmarksAsync(load, store, name, FrameMax + 1, StretchStarts())
            : Task.CompletedTask;
        SetBookmarkMarks();
        RaiseBookmarks();
        Raise(nameof(CanBookmark));
    }

    /// <summary>The first step of each timeline stretch: a bookmark keeps its place in its stretch (<see cref="ReplayBookmark"/>).</summary>
    private List<int> StretchStarts() => _jumps.Select(j => j.Start).ToList();

    /// <summary>
    /// Reads the replay's bookmarks off the UI thread. A replay written again with another step count moves them to their
    /// place in their stretch, or drops the ones whose place is gone; the history is changed to match.
    /// </summary>
    private async Task LoadBookmarksAsync(int load, HistoryStore store, string name, int frameCount, IReadOnlyList<int> starts)
    {
        IReadOnlyList<int> frames;
        try
        {
            frames = await Task.Run(() =>
            {
                var saved = store.Bookmarks(name);
                var kept = saved
                    .Select(b => b.Resolve(frameCount, starts))
                    .OfType<int>()
                    .Distinct()
                    .Select(f => ReplayBookmark.At(f, frameCount, starts))
                    .ToList();
                if (!kept.SequenceEqual(saved))
                {
                    store.ReplaceBookmarks(name, kept);
                }

                return (IReadOnlyList<int>)kept.Select(b => b.Frame).ToList();
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
        {
            Diag.Error("Reading replay bookmarks failed", ex);
            return;
        }

        if (load != _bookmarkLoad)
        {
            return;
        }

        _bookmarks.UnionWith(frames);
        SetBookmarkMarks();
        RaiseBookmarks();
    }

    /// <summary>Writes a bookmark off the UI thread; a failure takes it back on this page and says so in the header.</summary>
    private async Task SaveBookmarkAsync(int load, HistoryStore store, string name, ReplayBookmark bookmark, bool on)
    {
        try
        {
            await Task.Run(() => store.SetBookmark(name, bookmark, on)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or UnauthorizedAccessException)
        {
            Diag.Error("Saving a replay bookmark failed", ex);
            if (load == _bookmarkLoad)
            {
                Mark(bookmark.Frame, !on);
                ShowHeaderStatus(Loc.T("Match.Player.BookmarkFailed"), "Loss", IconFailed, folder: false);
            }
        }
    }

    /// <summary>Puts a bookmark on a step of this page or takes it off; the timeline's marks change only here.</summary>
    private void Mark(int frame, bool on)
    {
        if (on)
        {
            _bookmarks.Add(frame);
        }
        else
        {
            _bookmarks.Remove(frame);
        }

        SetBookmarkMarks();
        RaiseBookmarks();
    }

    /// <summary>The timeline's marks: a new list only when a bookmark is put on or taken off, not at every step.</summary>
    private void SetBookmarkMarks() => BookmarkMarks = _bookmarks.Select(f => new MatchBookmarkRow(f, BookmarkText(f))).ToList();

    /// <summary>The "Turn" list from the timeline's stretches (both modes), built once per game.</summary>
    private void ShowJumps()
    {
        var spans = _page?.Timeline ?? [];
        var mulligan = FrameOf(ReplayActionKind.Mulligan);
        var jumps = new List<MatchJumpRow>(_segments.Count);
        for (var i = 0; i < _segments.Count && i < spans.Count; i++)
        {
            var (segment, span) = (_segments[i], spans[i]);
            var isMulligan = span.Phase == MatchPhase.Mulligan;
            var text = span.Phase switch
            {
                MatchPhase.Mulligan => Loc.T("Match.Step.Mulligan"),
                MatchPhase.Turn => Loc.F("Match.Step.Turn", span.Label, Loc.T(span.Local ? "Match.You" : "Match.Opponent")),
                MatchPhase.Shop => Loc.F("Match.Bg.Title.Shop", span.Turn),
                MatchPhase.Combat => Loc.F("Match.Bg.Title.Combat", span.Turn),
                MatchPhase.End => Loc.T("Match.Bg.Title.End"),
                _ => Loc.T(HasBattlegroundsSteps ? "Match.Bg.Title.Start" : "Match.Step.Start"),
            };

            // The mulligan goes to its choice step (where the offered cards show), not to the game's start.
            var target = isMulligan && mulligan >= segment.FirstFrame && mulligan <= segment.LastFrame ? mulligan : segment.FirstFrame;
            jumps.Add(new MatchJumpRow(target, text, isMulligan, segment.Tooltip.Length > 0 ? segment.Tooltip : text, segment.Look) { Start = segment.FirstFrame });
        }

        Jumps = jumps;
        Raise(nameof(HasMulligan));
        Raise(nameof(JumpIndex));
    }

    private void ClearNavigation()
    {
        Jumps = [];
        TurnsOpen = false;
        SetReplay(null, null, null);
    }

    /// <summary>The step changed: the list's entry (unless it is open) and the bookmark controls follow.</summary>
    private void MoveNavigation()
    {
        if (!_turnsOpen)
        {
            Raise(nameof(JumpIndex));
        }

        var current = _bookmarks.Contains(_frame) ? _frame : -1;
        if (current != _bookmarkCurrent)
        {
            RaiseBookmarks();
            return;
        }

        RaiseBookmarkState();
    }

    private void RaiseBookmarks()
    {
        _bookmarkCurrent = _bookmarks.Contains(_frame) ? _frame : -1;
        BookmarkRows = _bookmarks.Select(f => new MatchBookmarkRow(f, BookmarkText(f), f == _frame)).ToList();
        Raise(nameof(HasBookmarks));
        Raise(nameof(BookmarkCountText));
        Raise(nameof(BookmarksTip));
        RaiseBookmarkState();
    }

    private void RaiseBookmarkState()
    {
        Raise(nameof(IsBookmarked));
        Raise(nameof(BookmarkTip));
        Raise(nameof(BookmarkGlyph));
        Raise(nameof(HasPreviousBookmark));
        Raise(nameof(HasNextBookmark));
    }

    /// <summary>"Step 34 · turn 7", "Step 3 · mulligan", or "Step 1" before the first turn (start, hero choice).</summary>
    private string BookmarkText(int frame)
    {
        var step = (frame + 1).ToString(CultureInfo.CurrentCulture);
        var mark = _page?.Marks.FirstOrDefault(m => m.Frame == frame);
        if (mark?.Phase == MatchPhase.Mulligan)
        {
            return Loc.F("Match.Player.BookmarkMulligan", step);
        }

        // Constructed: the player's turn of the step; Battlegrounds: the step's turn.
        var turn = _page?.Replay is not null ? TurnAt(frame) : mark?.Turn ?? 0;
        return turn > 0 && mark?.Phase is not MatchPhase.Start
            ? Loc.F("Match.Player.BookmarkTurn", step, turn)
            : Loc.F("Match.Player.BookmarkStep", step);
    }

    private void OnHeaderCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryCard.HasReplay))
        {
            Raise(nameof(ShowHeaderActions));
        }
        else if (e.PropertyName == nameof(LibraryCard.Confirming))
        {
            Raise(nameof(HeaderConfirming));
        }
    }

    /// <summary>The header's star or Share ran: the library's status line (now and when the copy is done) shows here too.</summary>
    private void MirrorLibraryStatus()
    {
        if (!_libraryWatched)
        {
            _libraryWatched = true;
            _owner.Library.PropertyChanged += OnLibraryChanged;
        }

        _mirrorLibraryStatus = true;
        CopyLibraryStatus();
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_mirrorLibraryStatus && e.PropertyName is nameof(ReplayLibraryViewModel.Status) or nameof(ReplayLibraryViewModel.StatusKey)
                or nameof(ReplayLibraryViewModel.StatusGlyph) or nameof(ReplayLibraryViewModel.HasStatusFolder))
        {
            CopyLibraryStatus();
        }
    }

    private void CopyLibraryStatus()
    {
        var library = _owner.Library;
        ShowHeaderStatus(library.Status, library.StatusKey, library.StatusGlyph, library.HasStatusFolder);
    }

    private void ShowHeaderStatus(string text, string key, string glyph, bool folder)
    {
        HeaderStatusKey = key;
        HeaderStatusGlyph = glyph;
        HasHeaderStatusFolder = folder;
        HeaderStatus = text;
    }

    /// <summary>
    /// The header's status goes at the next action, another tab, another game or Back; a star or Share result it showed
    /// does not come back on the History page either.
    /// </summary>
    private void ClearHeaderStatus()
    {
        if (_mirrorLibraryStatus)
        {
            _mirrorLibraryStatus = false;
            _owner.Library.ClearStatus();
        }

        ShowHeaderStatus(string.Empty, string.Empty, string.Empty, folder: false);
    }
}
