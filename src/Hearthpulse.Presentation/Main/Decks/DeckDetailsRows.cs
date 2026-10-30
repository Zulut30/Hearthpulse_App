#if HEARTHPULSE_WPF
using Hearthpulse.App.Controls;
#else
using Hearthpulse.UI.Controls;
#endif
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Decks;
#else
namespace Hearthpulse.UI.Main.Decks;
#endif

/// <summary>A game on the deck's win-rate line, with what its hover card shows.</summary>
public sealed record DeckGamePoint(
    double ChartValue,
    string ChartLabel,
    string Result,
    string Title,
    string Detail,
    string When,
    string SoFar,
    string OpponentHeroCardId) : IChartValue
{
    /// <summary>The result as the player reads it; <see cref="Result"/> keeps the stored value for the colour.</summary>
    public string ResultText => DeckDetailsViewModel.ResultText(Result);
}

/// <summary>A matchup: an opponent class, or an archetype of it (indented under the classes).</summary>
public sealed record DeckMatchupRow(
    string Key,
    string Label,
    string HeroCardId,
    string Games,
    string Record,
    string WinRate,
    double WinFraction,
    bool IsArchetype,
    bool Selected,
    bool Few,
    int GameCount = 0)
{
    /// <summary>The win rate's colour: +1 from 53 %, -1 up to 47 %, 0 between them and when there is no rate.</summary>
    public int WinSign => WinRate == "—" ? 0 : WinFraction >= 0.53 ? 1 : WinFraction <= 0.47 ? -1 : 0;

    /// <summary>+1 on the best opponent class of the table, -1 on the worst (enough games only), else 0.</summary>
    public int WinMark { get; init; }

    public bool WinBest => WinMark > 0;

    public bool WinWorst => WinMark < 0;

    /// <summary>The value of a sortable column: "Name", "Games" or "WinRate".</summary>
    internal IComparable? SortValue(string column) => column switch
    {
        "Name" => Label,
        "Games" => GameCount,
        _ => WinRate == "—" ? null : WinFraction,
    };
}

/// <summary>One cost of the main version's mana curve ("0" … "7+"): cards at that cost and the bar's share of the tallest.</summary>
public sealed record ManaCurveBar(string Label, int Count, double Fraction)
{
    /// <summary>No card at this cost: the stub and its count are dimmed.</summary>
    public bool Empty => Count == 0;
}

/// <summary>
/// A card of the main version as the deck tracker lists it: cost gem, name over its art, rarity edge and the copies
/// ("2", "★" for a legendary, empty for one copy).
/// </summary>
public sealed record DeckListRow(string CardId, string Name, int Cost, string Rarity, string Count)
{
    /// <summary>The star then takes the legendary colour.</summary>
    public bool Legendary => Rarity == "LEGENDARY";
}

/// <summary>
/// One card in the mulligan and card tables. Signs colour a win rate against the deck's own (+1 above, -1 below);
/// marks flag the best (+1) and the worst (-1) card of a column among the cards with enough games.
/// </summary>
public sealed record DeckCardStatRow(
    string CardId,
    string Name,
    int Cost,
    string Copies,
    bool InMain,
    bool FewMulligan,
    bool FewDrawn,
    string Offered,
    string KeepRate,
    string KeptWinRate,
    int KeptSign,
    string ReplacedWinRate,
    int ReplacedSign,
    string MetaKeep,
    string MetaWinRate,
    string Drawn,
    string DrawnWinRate,
    int DrawnSign,
    string Played,
    string PlayedWinRate,
    int PlayedSign,
    string AverageTurn)
{
    /// <summary>The raw numbers behind the columns, for sorting and the bars (null: no value).</summary>
    public DeckCardNumbers Numbers { get; init; } = DeckCardNumbers.None;

    public int KeptMark { get; init; }

    public int ReplacedMark { get; init; }

    public int DrawnMark { get; init; }

    public int PlayedMark { get; init; }

    public bool KeptBest => KeptMark > 0;

    public bool KeptWorst => KeptMark < 0;

    public bool ReplacedBest => ReplacedMark > 0;

    public bool ReplacedWorst => ReplacedMark < 0;

    public bool DrawnBest => DrawnMark > 0;

    public bool DrawnWorst => DrawnMark < 0;

    public bool PlayedBest => PlayedMark > 0;

    public bool PlayedWorst => PlayedMark < 0;

    /// <summary>Share of the opening hands with the card that kept it (0..1), for the keep bar.</summary>
    public double KeepFraction => Numbers.KeepRate ?? 0;

    /// <summary>The value of a sortable column of <see cref="DeckDetailsViewModel.MulliganSort"/> or <see cref="DeckDetailsViewModel.CardsSort"/>.</summary>
    internal IComparable? SortValue(string column) => column switch
    {
        "Card" => (Cost, Name),
        "Offered" => Numbers.Offered,
        "Kept" => Numbers.KeepRate,
        "KeptWinRate" => Numbers.KeptWinRate,
        "ReplacedWinRate" => Numbers.ReplacedWinRate,
        "MetaKept" => Numbers.MetaKeep,
        "MetaWinRate" => Numbers.MetaWinRate,
        "Drawn" => Numbers.Drawn,
        "DrawnWinRate" => Numbers.DrawnWinRate,
        "Played" => Numbers.Played,
        "PlayedWinRate" => Numbers.PlayedWinRate,
        "AverageTurn" => Numbers.AverageTurn,
        _ => null,
    };
}

/// <summary>A card's mulligan and card numbers as numbers (rates 0..1; null where there is none).</summary>
public sealed record DeckCardNumbers(
    int Offered,
    double? KeepRate,
    double? KeptWinRate,
    double? ReplacedWinRate,
    double? MetaKeep,
    double? MetaWinRate,
    int Drawn,
    double? DrawnWinRate,
    int Played,
    double? PlayedWinRate,
    double? AverageTurn)
{
    public static DeckCardNumbers None { get; } = new(0, null, null, null, null, null, 0, null, 0, null, null);
}

/// <summary>A card whose copies differ between two lists: its tile, name and "+2" / "−1" (the sign colours it).</summary>
public sealed record CardDiffRow(string CardId, string Name, string Count, int Sign);

/// <summary>
/// A version of the deck with its own results and its cards against the main version. <see cref="Few"/>: too few games
/// to tell, the win bar is faded. <see cref="DiffOpen"/>: its cards against the main version are shown.
/// </summary>
public sealed record DeckVersionRow(
    string Key,
    string Name,
    bool IsMain,
    string Detail,
    string Record,
    string WinRate,
    double WinFraction,
    int Games,
    bool Few,
    string Changes,
    IReadOnlyList<CardDiffRow> Diff,
    bool DiffOpen,
    bool CanManage)
{
    public bool CanMakeMain => CanManage && !IsMain;

    public bool HasDiff => Diff.Count > 0;

    public string DiffAction => DiffOpen ? DeckVersionsText.HideDiff : DeckVersionsText.ShowDiff;

    public string FewGamesTip => DeckVersionsText.FewGamesTip;
}

/// <summary>
/// Another deck that looks like a version of this one. On the deck page it also has its cards against this deck's main
/// version (<see cref="Changes"/> "+2 / −2 cards", <see cref="Diff"/>), shown while <see cref="DiffOpen"/>.
/// </summary>
public sealed record DeckSuggestionRow(string FirstKey, string SecondKey, string Title, string Detail)
{
    public string Changes { get; init; } = string.Empty;

    public IReadOnlyList<CardDiffRow> Diff { get; init; } = [];

    public bool DiffOpen { get; init; }
}

/// <summary>A deck of the same class and format that can be added to this one by hand (most alike first).</summary>
public sealed record MergeCandidateRow(string Key, string Name, string HeroCardId, string Detail, string Shared, bool Dismissed, bool Selected);

/// <summary>A recent game of the deck.</summary>
public sealed record DeckGameRow(string Key, string When, string Result, string Opponent, string OpponentHeroCardId, string Detail)
{
    /// <summary>The result as the player reads it; <see cref="Result"/> keeps the stored value for the colour.</summary>
    public string ResultText => DeckDetailsViewModel.ResultText(Result);
}
