#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.ComponentModel;
using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

// The rows the Standard/Wild meta tabs bind to (MainViewModel.Meta.cs builds them): filter choices, an archetype of
// the tier list, a deck list, a mulligan card and the matchup heat map. A tier's head row is in TierRows.cs.

public sealed record MetaChoice(string Key, string Label, bool Selected) : IImmutableView;

/// <summary>
/// One archetype of the Standard/Wild tier list, as hearthpulse.net shows it: tier, class portrait, name (the English
/// one under a translated name), win rate on a colour scale with its 95 % interval, popularity bar and games.
/// </summary>
public sealed record MetaArchetypeRow(
    int Id,
    int Rank,
    string Name,
    string EnglishName,
    string HeroId,
    string Detail,
    string Tier,
    string WinRate,
    string WinRateTip,
    double Edge,
    int WinSign,
    double BarFraction,
    string Popularity,
    double PopularityFraction,
    string Games,
    string First,
    string Coin) : IImmutableView, INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the source split this archetype's games by who went first.</summary>
    public bool HasPlaySplit => First != "—" || Coin != "—";

    /// <summary>The win rates going first and on the coin in one cell ("53% · 49%"); a dash without either.</summary>
    public string PlaySplit => HasPlaySplit ? First + " · " + Coin : "—";

    /// <summary>
    /// Whether the list shows the play split at all (some row of it has one): the same for every row of a list, kept in
    /// the row so its cell reads its own row and not, through a search up the tree, the page.
    /// </summary>
    public bool ShowsPlaySplit { get; init; }

    /// <summary>
    /// The full length of the win-rate bar in both views' row (design.md 1: the bar's room in its cell). A number of
    /// the view kept beside the row on purpose: the row gives its bars' lengths ready, so a row costs no converter call
    /// per bar. MainWindowLookTests holds both markups to these two numbers.
    /// </summary>
    public const double WinBarLength = 56;

    /// <summary>The full length of the popularity bar in both views' row.</summary>
    public const double PopularityBarLength = 44;

    /// <summary>The length of the win-rate bar: computed once here, not by a converter in each row.</summary>
    public double WinBar => BarFraction * WinBarLength;

    /// <summary>The length of the popularity bar.</summary>
    public double PopularityBar => PopularityFraction * PopularityBarLength;

    /// <summary>
    /// The same row: what it shows, not whether it is chosen or who listens to it. A list changed in place
    /// (<see cref="SyncList{T}"/>) keeps a row that is equal to the new one, with its container and its choice.
    /// </summary>
    public bool Equals(MetaArchetypeRow? other) =>
        other is not null && Id == other.Id && Rank == other.Rank && Name == other.Name && EnglishName == other.EnglishName && HeroId == other.HeroId
        && Detail == other.Detail && Tier == other.Tier && WinRate == other.WinRate && WinRateTip == other.WinRateTip && Edge.Equals(other.Edge)
        && WinSign == other.WinSign && BarFraction.Equals(other.BarFraction) && Popularity == other.Popularity
        && PopularityFraction.Equals(other.PopularityFraction) && Games == other.Games && First == other.First && Coin == other.Coin
        && ShowsPlaySplit == other.ShowsPlaySplit;

    public override int GetHashCode() => HashCode.Combine(Id, Rank, Name, Tier, WinRate, Popularity, Games);

    /// <summary>The archetype whose mulligan guide and lists the side card shows.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

/// <summary>The sort arrow of each sortable column ("▼", "▲" or empty), so a header shows how the table is ordered.</summary>
public sealed record MetaSortArrows(string Tier, string Name, string WinRate, string Popularity, string Games) : IImmutableView;

public sealed record MetaDeckRow(string Name, string Class, string HeroId, string WinRate, double Edge, string Games, string Dust, string Code) : IImmutableView;

/// <summary>One card of the mulligan guide; <see cref="Seen"/> is its tooltip ("Seen in 1,234 opening hands").</summary>
public sealed record MulliganRow(string CardId, string Name, string WinRate, double Edge, string KeepRate, string Seen) : IImmutableView;

/// <summary>A column of the matchup heat map: the opponent archetype's portrait, short name and full name as tooltip.</summary>
public sealed record MatchupHead(string Name, string HeroId, string Tip) : IImmutableView;

/// <summary>
/// One cell of the heat map: the win rate as a number, <see cref="Edge"/> (win rate − 50 %) for the colour and
/// <see cref="Heat"/> for the tint's opacity (up to 0.6, so the number stays readable); no games leaves it blank.
/// </summary>
public sealed record MatchupCellView(string Text, double Edge, double Heat, string Tip) : IImmutableView;

/// <summary>One archetype of the heat map against every column.</summary>
public sealed record MatchupMatrixRow(string Name, string HeroId, string WinRate, double Edge, IReadOnlyList<MatchupCellView> Cells) : IImmutableView;
