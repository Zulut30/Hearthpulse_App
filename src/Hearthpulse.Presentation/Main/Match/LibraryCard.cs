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
public sealed record LibraryChip(string CardId, string Tooltip) : IImmutableView;

/// <summary>A minion of a Battlegrounds card's final board: attack and health badges under its portrait, golden, taunt, divine shield.</summary>
public sealed record LibraryMinion(string CardId, string Stats, bool Golden, bool Taunt, bool DivineShield, string Tooltip, int AttackValue = 0, int HealthValue = 0) : IImmutableView
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
public sealed record LibraryBoardView(IReadOnlyList<LibraryMinion> Shown, string More, string MoreTip) : IImmutableView;

/// <summary>
/// The parts of a Battlegrounds card read from its replay's header: up to 3 chips (hero powers, trinkets) and "+N" with
/// the rest in its tooltip, and the final board, which shows as many minions as its room fits (<see cref="BoardAt"/>).
/// One object, so a card that gets its header tells the view once instead of once per part.
/// </summary>
public sealed record LibrarySummary(
    IReadOnlyList<LibraryChip> Chips,
    string ChipsMore,
    string ChipsMoreTip,
    IReadOnlyList<LibraryMinion> Board) : IImmutableView
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
    private string _confirmLabel = string.Empty;

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

    /// <summary>The confirm button's word for the question asked: "Delete", "Upload", "Remove from site".</summary>
    public string ConfirmLabel
    {
        get => _confirmLabel;
        internal set => Set(ref _confirmLabel, value);
    }

    /// <summary>What the confirm button does.</summary>
    internal Action? PendingConfirm { get; set; }

    /// <summary>Sets the parts read from the replay's header: chips (up to 3, then "+N") and the final board.</summary>
    internal void SetSummary(IReadOnlyList<LibraryChip> chips, IReadOnlyList<LibraryMinion> board) =>
        Summary = LibrarySummary.Of(chips, board);
}
