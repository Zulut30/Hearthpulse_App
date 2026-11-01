#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.IO;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>A hero power, trinket or hand card of a library card: the card (full card on hover) and its name.</summary>
public sealed record LibraryChip(string CardId, string Tooltip);

/// <summary>A minion of a Battlegrounds card's final board: stats under its portrait, golden, taunt, divine shield.</summary>
public sealed record LibraryMinion(string CardId, string Stats, bool Golden, bool Taunt, bool DivineShield, string Tooltip);

/// <summary>
/// One game of the replay library (<c>docs/replay-library.md</c>): the History row (Recent) or the file's summary
/// (Saved, Imported). The final board, hand and chips come from the replay's header and may arrive after the card is
/// shown; the star and the confirmation change in place.
/// </summary>
public sealed class LibraryCard : Observable
{
    private IReadOnlyList<LibraryChip> _chips = [];
    private string _chipsMore = string.Empty;
    private IReadOnlyList<LibraryMinion> _board = [];
    private IReadOnlyList<LibraryChip> _hand = [];
    private string _handMore = string.Empty;
    private string _handMoreTip = string.Empty;
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

    public IReadOnlyList<LibraryChip> Chips { get => _chips; private set => Set(ref _chips, value); }

    public string ChipsMore { get => _chipsMore; private set => Set(ref _chipsMore, value); }

    public IReadOnlyList<LibraryMinion> Board { get => _board; private set => Set(ref _board, value); }

    public IReadOnlyList<LibraryChip> Hand { get => _hand; private set => Set(ref _hand, value); }

    public string HandMore { get => _handMore; private set => Set(ref _handMore, value); }

    public string HandMoreTip { get => _handMoreTip; private set => Set(ref _handMoreTip, value); }

    /// <summary>A replay file is there (or its starred copy): star and Share show, else the "no replay" mark.</summary>
    public bool HasReplay
    {
        get => _hasReplay;
        internal set
        {
            if (Set(ref _hasReplay, value))
            {
                Raise(nameof(NoReplay));
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

    /// <summary>Sets the parts read from the replay's header: chips (up to 3, then "+N"), the final board, the hand (up to 3).</summary>
    internal void SetSummary(IReadOnlyList<LibraryChip> chips, IReadOnlyList<LibraryMinion> board, IReadOnlyList<LibraryChip> hand)
    {
        Chips = chips.Take(3).ToList();
        ChipsMore = chips.Count > 3 ? $"+{chips.Count - 3}" : string.Empty;
        Board = board;
        Hand = hand.Take(3).ToList();
        HandMore = hand.Count > 3 ? $"+{hand.Count - 3}" : string.Empty;
        HandMoreTip = string.Join(", ", hand.Skip(3).Select(c => c.Tooltip));
    }
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
    private LibraryCard? _selected;
    private int _savedCount;
    private int _importedCount;
    private string _status = string.Empty;
    private string _statusKey = string.Empty;
    private string _statusGlyph = string.Empty;
    private string? _statusFolder;
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
            if (_statusFolder is { } folder)
            {
                OpenInShell(folder);
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
                Raise(nameof(EmptyText));
                Raise(nameof(EmptyGlyph));
            }
        }
    }

    /// <summary>The card marked as selected: just imported, or sent here from a deck page.</summary>
    public LibraryCard? SelectedCard { get => _selected; private set => Set(ref _selected, value); }

    public bool IsEmpty => _cards.Count == 0 && !_loading;

    public string EmptyText => _owner.HistoryFilter != "All" && TabCount > 0 ? Loc.T("Library.Empty.Filter")
        : IsSaved ? Loc.T("Library.Empty.Saved")
        : IsImported ? Loc.T("Library.Empty.Imported")
        : Loc.T("Main.Home.NoGames");

    public string EmptyGlyph => _owner.HistoryFilter != "All" && TabCount > 0 ? "\U000F02DA" : IsSaved ? "\U000F04D2" : IsImported ? "\U000F02FA" : "\U000F02DA";

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

    /// <summary>Avalonia without a file clipboard: the status line links the copy's folder.</summary>
    public bool HasStatusFolder => _statusFolder is not null;

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

    public RelayCommand SetTab { get; }

    public RelayCommand Open { get; }

    public RelayCommand ToggleStar { get; }

    public RelayCommand Share { get; }

    public RelayCommand Delete { get; }

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

    /// <summary>The view put the shared copy on the clipboard: as a file, only its path (<paramref name="folder"/> linked), or not at all.</summary>
    public void ShareDone(bool copied, bool pathOnly = false, string? folder = null)
    {
        if (!copied)
        {
            Failed(Loc.T("Main.Meta.ClipboardBusy"));
            return;
        }

        ShowStatus(pathOnly ? Loc.T("Library.SharedPath") : Loc.T("Library.Shared"), "Win", "\U000F05E1", pathOnly ? folder : null);
    }

    internal void RequestShare(string path) => ShareFileRequested?.Invoke(path);

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
        Cards = cards;
        SelectedCard = select;
        Raise(nameof(IsEmpty));
    }

    internal void BeginLoading()
    {
        _loading = true;
        Raise(nameof(IsEmpty));
    }

    internal void Select(LibraryCard? card) => SelectedCard = card;

    internal void Remove(LibraryCard card) => Cards = _cards.Where(c => c != card).ToList();

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

    private void ShowStatus(string text, string key, string glyph, string? folder = null)
    {
        Status = text;
        StatusKey = key;
        StatusGlyph = glyph;
        if (_statusFolder != folder)
        {
            _statusFolder = folder;
            Raise(nameof(HasStatusFolder));
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
