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

/// <summary>
/// The History page as a library (<c>docs/replay-library.md</c>): tabs Recent / Saved / Imported with their cards, the
/// star, Share, import and delete, and the status line under the tabs. The cards are built by the main window's model
/// (<c>MainViewModel.Library.cs</c>), which also opens them; the views do the platform parts (file picker, clipboard)
/// when <see cref="PickFileRequested"/> and <see cref="ShareFileRequested"/> fire.
/// </summary>
public sealed partial class ReplayLibraryViewModel : Observable
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
                    Ask(card, Loc.T("Library.Site.ConfirmRemove"), Loc.T("Library.Site.ConfirmRemoveYes"), () => _owner.RemoveLibraryCardFromSite(card, name));
                }
                else
                {
                    Ask(card, Loc.T("Library.Site.ConfirmUpload"), Loc.T("Library.Site.ConfirmUploadYes"), () => _owner.UploadLibraryCard(card, name));
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
                Ask(card, Loc.T("Library.ConfirmDelete"), Loc.T("Library.ConfirmYes"), () => _owner.DeleteImported(card, name));
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
        internal set => Set(ref _savedCount, value);
    }

    public int ImportedCount
    {
        get => _importedCount;
        internal set => Set(ref _importedCount, value);
    }

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
        Raise(nameof(EmptyText));
        Raise(nameof(EmptyGlyph));
    }

    /// <summary>Asks <paramref name="question"/> in the card; its confirm button reads <paramref name="confirm"/> and does <paramref name="action"/>.</summary>
    internal static void Ask(LibraryCard card, string question, string confirm, Action action)
    {
        card.PendingConfirm = action;
        card.ConfirmLabel = confirm;
        card.ConfirmText = question;
    }
}
