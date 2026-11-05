#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.IO;
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>A hero power or trinket of a library card: the card (full card on hover) and its name.</summary>
public sealed record LibraryChip(string CardId, string Tooltip);

/// <summary>A minion of a Battlegrounds card's final board: attack and health badges under its portrait, golden, taunt, divine shield.</summary>
public sealed record LibraryMinion(string CardId, string Stats, bool Golden, bool Taunt, bool DivineShield, string Tooltip, int AttackValue = 0, int HealthValue = 0)
{
    /// <summary>The attack badge: the number, shortened from 1000 ("1,4k") so the board never cuts it.</summary>
    public string Attack => Short(AttackValue);

    /// <summary>The health badge, shortened like <see cref="Attack"/>.</summary>
    public string Health => Short(HealthValue);

    /// <summary>Up to 999 as it is, then thousands with one decimal ("1,4k") and from 10 000 whole ("12k").</summary>
    internal static string Short(int value) => value switch
    {
        < 1000 => value.ToString(CultureInfo.CurrentCulture),
        < 10000 => (value / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + "k",
        _ => (value / 1000).ToString(CultureInfo.CurrentCulture) + "k",
    };
}

/// <summary>
/// The final board as one width shows it: the minions that fit whole and "+N" for the rest, its tooltip naming them.
/// </summary>
public sealed record LibraryBoardView(IReadOnlyList<LibraryMinion> Shown, string More, string MoreTip);

/// <summary>
/// The parts of a Battlegrounds card read from its replay's header: up to 3 chips (hero powers, trinkets) and "+N" with
/// the rest in its tooltip, and the final board, which shows as many minions as its room fits (<see cref="BoardAt"/>).
/// One object, so a card that gets its header tells the view once instead of once per part.
/// </summary>
public sealed record LibrarySummary(
    IReadOnlyList<LibraryChip> Chips,
    string ChipsMore,
    string ChipsMoreTip,
    IReadOnlyList<LibraryMinion> Board)
{
    /// <summary>Chips shown before the rest goes into "+N".</summary>
    public const int Shown = 3;

    /// <summary>A minion of the board in the views: 52 wide (its portrait over its attack and health badges) and 1 apart.</summary>
    public const double MinionRoom = 53;

    /// <summary>The board's "+N" pill ("+6" at 11 px with the pill's padding) and the gap before it.</summary>
    public const double MoreRoom = 36;

    public static LibrarySummary Empty { get; } = new([], string.Empty, string.Empty, []);

    /// <summary>The board cut after each count of minions shown (0..all): one list per count, kept for every resize.</summary>
    private IReadOnlyList<LibraryBoardView> Boards { get; init; } = [new([], string.Empty, string.Empty)];

    public static LibrarySummary Of(IReadOnlyList<LibraryChip> chips, IReadOnlyList<LibraryMinion> board) => new(
        chips.Take(Shown).ToList(),
        More(chips.Count),
        string.Join(", ", chips.Skip(Shown).Select(c => c.Tooltip)),
        board)
    {
        Boards = Enumerable.Range(0, board.Count + 1).Select(shown => new LibraryBoardView(
            board.Take(shown).ToList(),
            shown < board.Count ? $"+{board.Count - shown}" : string.Empty,
            string.Join('\n', board.Skip(shown).Select(m => m.Tooltip)))).ToList(),
    };

    /// <summary>
    /// How many minions of <paramref name="count"/> fit whole in <paramref name="width"/>: all when they do, else as many
    /// as leave room for the "+N" pill. Never a minion cut at the edge.
    /// </summary>
    public static int BoardFits(double width, int count)
    {
        if (count <= 0 || double.IsNaN(width) || count * MinionRoom <= width)
        {
            return Math.Max(0, count);
        }

        return Math.Clamp((int)Math.Floor((width - MoreRoom) / MinionRoom), 0, count);
    }

    /// <summary>The board as <paramref name="width"/> shows it; the same object for every width that fits as many.</summary>
    public LibraryBoardView BoardAt(double width) => Boards[Math.Min(BoardFits(width, Board.Count), Boards.Count - 1)];

    private static string More(int count) => count > Shown ? $"+{count - Shown}" : string.Empty;
}

/// <summary>The actions a key takes on a focused game card (<see cref="ReplayLibraryViewModel.CardKeyFor"/>).</summary>
public enum CardKey
{
    Star,
    Share,
    Delete,
    OpenOnSite,
}

/// <summary>What a library card is doing with the site (plan 2.2).</summary>
public enum SiteWork
{
    None,
    Uploading,
    Removing,
}

/// <summary>
/// One game of the replay library (<c>docs/replay-library.md</c>): the History row (Recent) or the file's summary
/// (Saved, Imported). The final board and the chips come from the replay's header and may arrive after the card is
/// shown; the star and the confirmation change in place.
/// </summary>
public sealed class LibraryCard : Observable
{
    private LibrarySummary _summary = LibrarySummary.Empty;
    private bool _starred;
    private bool _hasReplay;
    private string _confirmText = string.Empty;

    public LibraryCard(ReplayShelf shelf, string? fileName, string? historyKey, bool battlegrounds)
    {
        Shelf = shelf;
        FileName = fileName;
        HistoryKey = historyKey;
        IsBattlegrounds = battlegrounds;
        _hasReplay = fileName is not null;
    }

    public ReplayShelf Shelf { get; }

    /// <summary>The replay file's name (in its shelf's folder; a History game's may be gone), null when none was recorded.</summary>
    public string? FileName { get; }

    /// <summary>The History game's key, when the card is one (Saved cards of a game still in History have it too).</summary>
    public string? HistoryKey { get; }

    public bool IsBattlegrounds { get; }

    public bool IsConstructed => !IsBattlegrounds;

    public string HeroCardId { get; init; } = string.Empty;

    /// <summary>BG: the hero; Constructed: the deck, else the class.</summary>
    public string Title { get; init; } = string.Empty;

    public string Caption { get; init; } = string.Empty;

    /// <summary>The mode's name ("Battlegrounds"), which a plain Battlegrounds caption leaves out: the search still finds it.</summary>
    internal string ModeText { get; init; } = string.Empty;

    /// <summary>Constructed: the opponent's hero portrait overlapping ours ("" when unknown).</summary>
    public string OpponentHeroCardId { get; init; } = string.Empty;

    public string Opponent { get; init; } = string.Empty;

    public string OpponentArchetype { get; init; } = string.Empty;

    public string Result { get; init; } = string.Empty;

    public string ResultKey { get; init; } = string.Empty;

    public string Rating { get; init; } = string.Empty;

    public int RatingValue { get; init; }

    /// <summary>When it was played and for how long ("today 14:32 · 18 min").</summary>
    public string When { get; init; } = string.Empty;

    public bool CanDelete => Shelf == ReplayShelf.Imported;

    /// <summary>Newest first: the start time (yyyyMMddHHmmss), else the file name.</summary>
    internal string SortKey { get; init; } = string.Empty;

    /// <summary>The views bind its parts (<c>Summary.Board</c>, ...): one notification when the header arrives.</summary>
    public LibrarySummary Summary { get => _summary; private set => Set(ref _summary, value); }

    public IReadOnlyList<LibraryChip> Chips => _summary.Chips;

    public string ChipsMore => _summary.ChipsMore;

    public IReadOnlyList<LibraryMinion> Board => _summary.Board;

    /// <summary>A replay file is there (or its starred copy): star and Share show, else the "no replay" mark.</summary>
    public bool HasReplay
    {
        get => _hasReplay;
        internal set
        {
            if (Set(ref _hasReplay, value))
            {
                Raise(nameof(NoReplay));
                Raise(nameof(CanUpload));
            }
        }
    }

    public bool NoReplay => !_hasReplay;

    public bool Starred
    {
        get => _starred;
        internal set
        {
            if (Set(ref _starred, value))
            {
                Raise(nameof(StarTip));
                Raise(nameof(StarGlyph));
            }
        }
    }

    /// <summary>MDI star (saved) or star-outline.</summary>
    public string StarGlyph => _starred ? "\U000F04CE" : "\U000F04D2";

    public string StarTip => _starred ? Loc.T("Library.Star.Remove") : Loc.T("Library.Star.Add");

    private string _siteUrl = string.Empty;

    /// <summary>The replay's public link once it was uploaded to the site (plan 2.2); empty when it was not.</summary>
    public string SiteUrl
    {
        get => _siteUrl;
        set
        {
            if (Set(ref _siteUrl, value))
            {
                Raise(nameof(OnSite));
                Raise(nameof(SiteGlyph));
                Raise(nameof(SiteTip));
            }
        }
    }

    public bool OnSite => _siteUrl.Length > 0;

    /// <summary>
    /// Only the player's own games go to the site (Recent, Saved): an imported replay is someone else's game, and the
    /// upload says "as you saw it".
    /// </summary>
    public bool CanUpload => _hasReplay && Shelf != ReplayShelf.Imported;

    private SiteWork _siteWork;

    /// <summary>An upload or a delete under way: the button then cancels the upload, or waits for the delete.</summary>
    public SiteWork SiteWork
    {
        get => _siteWork;
        internal set
        {
            if (Set(ref _siteWork, value))
            {
                Raise(nameof(SiteGlyph));
                Raise(nameof(SiteTip));
            }
        }
    }

    /// <summary>Cancels the upload under way (null when none).</summary>
    internal CancellationTokenSource? SiteCancel { get; set; }

    /// <summary>mdi-cloud-upload, mdi-cloud-off-outline once it is on the site, mdi-close while uploading, mdi-cloud-sync while deleting.</summary>
    public string SiteGlyph => _siteWork switch
    {
        SiteWork.Uploading => "\U000F0156",
        SiteWork.Removing => "\U000F063F",
        _ => OnSite ? "\U000F0164" : "\U000F0167",
    };

    public string SiteTip => _siteWork switch
    {
        SiteWork.Uploading => Loc.T("Library.Site.Uploading"),
        SiteWork.Removing => Loc.T("Library.Site.Removing"),
        _ => OnSite ? Loc.F("Library.Site.RemoveTip", _siteUrl) : Loc.T("Library.Site.UploadTip"),
    };

    /// <summary>The question asked inside the card before a delete (empty: none asked).</summary>
    public string ConfirmText
    {
        get => _confirmText;
        internal set
        {
            if (Set(ref _confirmText, value))
            {
                Raise(nameof(Confirming));
            }
        }
    }

    public bool Confirming => _confirmText.Length > 0;

    /// <summary>What "Delete" in the confirmation does.</summary>
    internal Action? PendingConfirm { get; set; }

    /// <summary>Sets the parts read from the replay's header: chips (up to 3, then "+N") and the final board.</summary>
    internal void SetSummary(IReadOnlyList<LibraryChip> chips, IReadOnlyList<LibraryMinion> board) =>
        Summary = LibrarySummary.Of(chips, board);
}

/// <summary>
/// The History page as a library (<c>docs/replay-library.md</c>): tabs Recent / Saved / Imported with their cards, the
/// star, Share, import and delete, and the status line under the tabs. The cards are built by the main window's model
/// (<c>MainViewModel.Library.cs</c>), which also opens them; the views do the platform parts (file picker, clipboard)
/// when <see cref="PickFileRequested"/> and <see cref="ShareFileRequested"/> fire.
/// </summary>
public sealed class ReplayLibraryViewModel : Observable
{
    private readonly MainViewModel _owner;
    private string _tab = "Recent";
    private IReadOnlyList<LibraryCard> _cards = [];

    /// <summary>The tab's cards before the search and the result filter (<see cref="Cards"/> is what they let through).</summary>
    private IReadOnlyList<LibraryCard> _all = [];
    private string _search = string.Empty;
    private string _resultFilter = "All";
    private LibraryCard? _selected;
    private int _savedCount;
    private int _importedCount;
    private string _status = string.Empty;
    private string _statusKey = string.Empty;
    private string _statusGlyph = string.Empty;
    private string? _statusFile;
    private bool _importing;
    private bool _loading;

    internal ReplayLibraryViewModel(MainViewModel owner, ReplayLibrary library)
    {
        _owner = owner;
        Files = library;
        SetTab = new RelayCommand(p =>
        {
            if (p is string tab && tab != _tab)
            {
                ClearStatus();
                Tab = tab;
                _owner.ShowLibraryTab();
            }
        });
        Open = new RelayCommand(p =>
        {
            if (p is LibraryCard card)
            {
                ClearStatus();
                _owner.OpenLibraryCard(card).Forget("Open a library game");
            }
        });
        ToggleStar = new RelayCommand(p =>
        {
            if (p is LibraryCard { FileName: { } name } card)
            {
                ClearStatus();
                _owner.ToggleLibraryStar(card, name);
            }
        });
        Share = new RelayCommand(p =>
        {
            if (p is LibraryCard { FileName: { } name } card)
            {
                ClearStatus();
                _owner.ShareLibraryCard(card, name);
            }
        });
        // Plan 2.2: upload only on this explicit action, after the confirmation in the card says what goes to the site.
        SiteAction = new RelayCommand(p =>
        {
            if (p is LibraryCard { SiteWork: SiteWork.Uploading, SiteCancel: { } cancel })
            {
                cancel.Cancel();
            }
            else if (p is LibraryCard { FileName: { } name, CanUpload: true, SiteWork: SiteWork.None } card)
            {
                ClearStatus();
                if (card.OnSite)
                {
                    Ask(card, Loc.T("Library.Site.ConfirmRemove"), () => _owner.RemoveLibraryCardFromSite(card, name));
                }
                else
                {
                    Ask(card, Loc.F("Library.Site.ConfirmUpload", ReplayUpload.KeepDays), () => _owner.UploadLibraryCard(card, name));
                }
            }
        });
        // The uploaded replay's page in the browser: a separate action, so a click on the cloud never opens and a click
        // here never deletes.
        OpenOnSite = new RelayCommand(p =>
        {
            if (p is LibraryCard { OnSite: true } card)
            {
                ClearStatus();
                _owner.OpenLibraryCardOnSite(card);
            }
        });
        Delete = new RelayCommand(p =>
        {
            if (p is LibraryCard { FileName: { } name, CanDelete: true } card)
            {
                ClearStatus();
                Ask(card, Loc.T("Library.ConfirmDelete"), () => _owner.DeleteImported(card, name));
            }
        });
        Confirm = new RelayCommand(p =>
        {
            if (p is LibraryCard { PendingConfirm: { } action } card)
            {
                card.ConfirmText = string.Empty;
                card.PendingConfirm = null;
                action();
            }
        });
        Cancel = new RelayCommand(p =>
        {
            if (p is LibraryCard card)
            {
                card.ConfirmText = string.Empty;
                card.PendingConfirm = null;
            }
        });
        Import = new RelayCommand(() =>
        {
            if (!_importing)
            {
                ClearStatus();
                PickFileRequested?.Invoke();
            }
        });
        OpenFolder = new RelayCommand(() => OpenInShell(IsImported ? Files.Imported.Directory : Files.Saved.Directory));
        OpenStatusFolder = new RelayCommand(() =>
        {
            if (_statusFile is { } file)
            {
                RevealInShell(file);
            }
        });
    }

    /// <summary>The view should let the player pick a .hpr or Power.log and hand it to <see cref="ImportPath"/>.</summary>
    public event Action? PickFileRequested;

    /// <summary>The view should put this file on the clipboard and report with <see cref="ShareDone"/>.</summary>
    public event Action<string>? ShareFileRequested;

    internal ReplayLibrary Files { get; }

    /// <summary>"Recent", "Saved" or "Imported".</summary>
    public string Tab
    {
        get => _tab;
        private set
        {
            if (Set(ref _tab, value))
            {
                Raise(nameof(IsRecent));
                Raise(nameof(IsSaved));
                Raise(nameof(IsImported));
                Raise(nameof(ShowFolder));
                Raise(nameof(Subtitle));
                Raise(nameof(EmptyText));
                Raise(nameof(EmptyGlyph));
            }
        }
    }

    public bool IsRecent => _tab == "Recent";

    public bool IsSaved => _tab == "Saved";

    public bool IsImported => _tab == "Imported";

    /// <summary>"Open the folder" shows on Saved and Imported.</summary>
    public bool ShowFolder => !IsRecent;

    public IReadOnlyList<LibraryCard> Cards
    {
        get => _cards;
        private set
        {
            if (Set(ref _cards, value))
            {
                Raise(nameof(IsEmpty));
                Raise(nameof(IsSearchEmpty));
                Raise(nameof(EmptyText));
                Raise(nameof(EmptyGlyph));
            }
        }
    }

    /// <summary>The card marked as selected: just imported, or sent here from a deck page.</summary>
    public LibraryCard? SelectedCard { get => _selected; private set => Set(ref _selected, value); }

    public bool IsEmpty => _cards.Count == 0 && !_loading;

    /// <summary>
    /// The search box (docs/replay-library.md): cards whose hero or class, opponent, its archetype, caption (mode, format,
    /// deck) or result hold the text, case ignored; empty shows all. Applies to every tab.
    /// </summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? string.Empty))
            {
                ApplySearch();
            }
        }
    }

    /// <summary>"All", "Win" or "Loss" (Battlegrounds: a top finish is a win, as on the cards).</summary>
    public string ResultFilter
    {
        get => _resultFilter;
        private set
        {
            if (Set(ref _resultFilter, value))
            {
                Raise(nameof(ResultAll));
                Raise(nameof(ResultWins));
                Raise(nameof(ResultLosses));
                ApplySearch();
            }
        }
    }

    public bool ResultAll => _resultFilter == "All";

    public bool ResultWins => _resultFilter == "Win";

    public bool ResultLosses => _resultFilter == "Loss";

    /// <summary>The search or the result filter hides some cards.</summary>
    public bool IsSearching => _search.Trim().Length > 0 || _resultFilter != "All";

    public RelayCommand SetResultFilter => _setResultFilter ??= new RelayCommand(p => ResultFilter = p as string is "Win" or "Loss" ? (string)p! : "All");

    /// <summary>Empties the search box and shows every result again.</summary>
    public RelayCommand ClearSearch => _clearSearch ??= new RelayCommand(ResetSearch);

    private RelayCommand? _setResultFilter;
    private RelayCommand? _clearSearch;

    /// <summary>
    /// The tab and the mode filter give games but the search or the result filter lets none through: the empty state says
    /// so and offers "Clear search" (an empty tab or mode keeps its own text, without the button).
    /// </summary>
    public bool IsSearchEmpty => IsEmpty && IsSearching && _all.Count > 0;

    public string EmptyText => IsSearching && _all.Count > 0
            ? _search.Trim() is { Length: > 0 } query ? Loc.F("Library.Search.Empty", query) : Loc.T("Library.Search.EmptyResult")
        : _owner.HistoryFilter != "All" && TabCount > 0 ? Loc.T("Library.Empty.Filter")
        : IsSaved ? Loc.T("Library.Empty.Saved")
        : IsImported ? Loc.T("Library.Empty.Imported")
        : Loc.T("Main.Home.NoGames");

    public string EmptyGlyph => IsSearching && _all.Count > 0 ? "\U000F0980"
        : _owner.HistoryFilter != "All" && TabCount > 0 ? "\U000F02DA" : IsSaved ? "\U000F04D2" : IsImported ? "\U000F02FA" : "\U000F02DA";

    /// <summary>The games History keeps, after the Recent tab's name.</summary>
    public int RecentCount => _owner.HistoryCount;

    public int SavedCount
    {
        get => _savedCount;
        internal set
        {
            if (Set(ref _savedCount, value))
            {
                Raise(nameof(Subtitle));
            }
        }
    }

    public int ImportedCount
    {
        get => _importedCount;
        internal set
        {
            if (Set(ref _importedCount, value))
            {
                Raise(nameof(Subtitle));
            }
        }
    }

    /// <summary>Under the History title: Recent keeps the History subtitle, the others say what their folder keeps.</summary>
    public string Subtitle => IsSaved ? Loc.P("Library.Subtitle.Saved", _savedCount)
        : IsImported ? Loc.P("Library.Subtitle.Imported", _importedCount)
        : _owner.HistorySubtitle;

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The status colour as a result key: "Win" success, "Loss" error, "" in progress.</summary>
    public string StatusKey { get => _statusKey; private set => Set(ref _statusKey, value); }

    public string StatusGlyph { get => _statusGlyph; private set => Set(ref _statusGlyph, value); }

    /// <summary>Avalonia without a file clipboard: the status line links the shared copy in its folder.</summary>
    public bool HasStatusFolder => _statusFile is not null;

    public bool Importing
    {
        get => _importing;
        private set
        {
            if (Set(ref _importing, value))
            {
                Raise(nameof(CanImport));
            }
        }
    }

    public bool CanImport => !_importing;

    /// <summary>Opens a game's page on the site (O on a card); a test hands in its own.</summary>
    internal Func<Uri, bool> OpenBrowser { get; set; } = SystemBrowser.Open;

    /// <summary>
    /// What a key does on a focused game card (review m3): S stars it, Shift+S shares it, Delete deletes an imported game
    /// (after the same question in the card), O opens its page on the site. <paramref name="key"/> is the key's name as
    /// both UIs spell it ("S", "Delete"); keys with Ctrl or Alt never reach here.
    /// </summary>
    public static CardKey? CardKeyFor(string key, bool shift) => (key, shift) switch
    {
        ("S", false) => CardKey.Star,
        ("S", true) => CardKey.Share,
        ("Delete", false) => CardKey.Delete,
        ("O", false) => CardKey.OpenOnSite,
        _ => null,
    };

    /// <summary>Does what the key does on <paramref name="card"/>; false when it does nothing there, so the key goes on.</summary>
    public bool HandleCardKey(LibraryCard card, string key, bool shift)
    {
        ArgumentNullException.ThrowIfNull(card);
        switch (CardKeyFor(key, shift))
        {
            case CardKey.Star when card.HasReplay:
                ToggleStar.Execute(card);
                return true;
            case CardKey.Share when card.HasReplay:
                Share.Execute(card);
                return true;
            case CardKey.Delete when card.CanDelete:
                Delete.Execute(card);
                return true;
            case CardKey.OpenOnSite when card.OnSite && Uri.TryCreate(card.SiteUrl, UriKind.Absolute, out var page):
                OpenBrowser(page);
                return true;
            default:
                return false;
        }
    }

    public RelayCommand SetTab { get; }

    public RelayCommand Open { get; }

    public RelayCommand ToggleStar { get; }

    public RelayCommand Share { get; }

    public RelayCommand Delete { get; }

    /// <summary>Opens an uploaded replay's page on the site (https on the site's host only).</summary>
    public RelayCommand OpenOnSite { get; }

    public RelayCommand SiteAction { get; }

    public RelayCommand Confirm { get; }

    public RelayCommand Cancel { get; }

    public RelayCommand Import { get; }

    public RelayCommand OpenFolder { get; }

    public RelayCommand OpenStatusFolder { get; }

    private int TabCount => IsSaved ? _savedCount : IsImported ? _importedCount : _owner.HistoryCount;

    /// <summary>Imports a picked or pasted file (the views call it).</summary>
    public void ImportPath(string path)
    {
        if (_importing)
        {
            return;
        }

        Importing = true;
        ShowStatus(Loc.T("Library.Importing"), string.Empty, "\U000F051F");
        _owner.ImportLibraryFile(path);
    }

    /// <summary>
    /// Ctrl+V on History: the clipboard's file or path (null: neither was there). Whether the file is there is asked off
    /// the UI thread: a network path can take seconds to answer.
    /// </summary>
    public async Task PasteAsync(string? path)
    {
        var exists = path is { Length: > 0 } file && await Task.Run(() => File.Exists(file)).ConfigureAwait(true);
        if (exists)
        {
            ImportPath(path!);
        }
        else
        {
            Failed(Loc.F("Library.ImportError", Loc.T("Library.ImportError.Clipboard")));
        }
    }

    /// <summary>The view put the shared copy on the clipboard as a file, or could not (the clipboard was busy).</summary>
    public void ShareDone(bool copied)
    {
        if (copied)
        {
            ShowStatus(Loc.T("Library.Shared"), "Win", "\U000F05E1");
        }
        else
        {
            Failed(Loc.T("Main.Meta.ClipboardBusy"));
        }
    }

    /// <summary>
    /// The platform's clipboard takes no files: the copy's folder opens with the file selected, for the player to drag
    /// it into the chat, and the status line links it again. Its path is never put on the clipboard: it holds the
    /// system user's name.
    /// </summary>
    public void ShareInFolder(string file)
    {
        ShowStatus(Loc.T("Library.SharedPath"), "Win", "\U000F05E1", file);
        RevealInShell(file);
    }

    internal void RequestShare(string path) => ShareFileRequested?.Invoke(path);

    /// <summary>The view should put this link on the clipboard (an uploaded replay).</summary>
    public event Action<string>? CopyLinkRequested;

    internal void Uploaded(string url)
    {
        CopyLinkRequested?.Invoke(url);
        ShowStatus(Loc.F("Library.Site.Uploaded", url), "Win", "\U000F05E1");
    }

    internal void ImportFinished() => Importing = false;

    internal void Succeeded(string text) => ShowStatus(text, "Win", "\U000F05E1");

    internal void Failed(string text) => ShowStatus(text, "Loss", "\U000F05D6");

    /// <summary>The status goes at the next tab, filter, opened match or action; there are no timers.</summary>
    internal void ClearStatus()
    {
        if (!_importing)
        {
            ShowStatus(string.Empty, string.Empty, string.Empty);
        }
    }

    internal void ShowTab(string tab) => Tab = tab;

    internal void SetCards(IReadOnlyList<LibraryCard> cards, LibraryCard? select = null)
    {
        _loading = false;
        _all = cards;
        if (select is not null && !Matches(select, _search, _resultFilter))
        {
            // A card chosen for the player (an import, a game sent from a deck page) must show.
            ResetSearch();
        }
        else
        {
            Cards = Filtered();
        }

        SelectedCard = select;
        Raise(nameof(IsEmpty));
        Raise(nameof(IsSearchEmpty));
    }

    /// <summary>The tab's cards before the search (a game sent here from a deck page is looked for among them).</summary>
    internal IReadOnlyList<LibraryCard> AllCards => _all;

    /// <summary>Clears the search and the result filter (a game sent here must show).</summary>
    internal void ResetSearch()
    {
        _search = string.Empty;
        _resultFilter = "All";
        Raise(nameof(Search));
        Raise(nameof(ResultFilter));
        Raise(nameof(ResultAll));
        Raise(nameof(ResultWins));
        Raise(nameof(ResultLosses));
        ApplySearch();
    }

    /// <summary>Whether a card passes the search text and the result filter.</summary>
    internal static bool Matches(LibraryCard card, string search, string result)
    {
        if (result != "All" && card.ResultKey != result)
        {
            return false;
        }

        var text = search.Trim();
        return text.Length == 0 || new[] { card.Title, card.Caption, card.ModeText, card.Opponent, card.OpponentArchetype, card.Result, card.When }
            .Any(field => field.Contains(text, StringComparison.CurrentCultureIgnoreCase));
    }

    private IReadOnlyList<LibraryCard> Filtered() =>
        IsSearching ? _all.Where(c => Matches(c, _search, _resultFilter)).ToList() : _all;

    private void ApplySearch()
    {
        Cards = Filtered();
        if (SelectedCard is { } selected && !_cards.Contains(selected))
        {
            SelectedCard = null;
        }

        Raise(nameof(IsSearching));
        Raise(nameof(IsEmpty));
        Raise(nameof(IsSearchEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(EmptyGlyph));
    }

    internal void BeginLoading()
    {
        _loading = true;
        Raise(nameof(IsEmpty));
        Raise(nameof(IsSearchEmpty));
    }

    internal void Select(LibraryCard? card)
    {
        if (card is not null && !_cards.Contains(card))
        {
            ResetSearch();
        }

        SelectedCard = card;
    }

    internal void Remove(LibraryCard card)
    {
        _all = _all.Where(c => c != card).ToList();
        Cards = _cards.Where(c => c != card).ToList();
    }

    /// <summary>The History subtitle or language changed: texts made from it follow.</summary>
    internal void Refresh()
    {
        Raise(nameof(RecentCount));
        Raise(nameof(Subtitle));
        Raise(nameof(EmptyText));
        Raise(nameof(EmptyGlyph));
    }

    internal static void Ask(LibraryCard card, string question, Action action)
    {
        card.PendingConfirm = action;
        card.ConfirmText = question;
    }

    private void ShowStatus(string text, string key, string glyph, string? file = null)
    {
        Status = text;
        StatusKey = key;
        StatusGlyph = glyph;
        if (_statusFile != file)
        {
            _statusFile = file;
            Raise(nameof(HasStatusFolder));
        }
    }

    /// <summary>Opens the folder of <paramref name="file"/> with the file selected where the platform can (Windows, macOS).</summary>
    private static void RevealInShell(string file)
    {
        try
        {
            var start = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"")
                : OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", ["-R", file])
                : new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(file) ?? file) { UseShellExecute = true };
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Error("Showing the shared replay copy failed", ex);
        }
    }

    private static void OpenInShell(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Diag.Error($"Opening {folder} failed", ex);
        }
    }
}
