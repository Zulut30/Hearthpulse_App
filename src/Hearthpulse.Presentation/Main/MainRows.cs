#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed record GameRow(
    string When,
    string Mode,
    string HeroCardId,
    string Hero,
    string Result,
    string ResultKey,
    string Detail,
    string Rating,
    int RatingValue,
    string Key = "")
{
    /// <summary>The mode and the game's details on one line: "Battlegrounds · Tier 5 · 11 turns".</summary>
    public string Summary => Detail.Length > 0 ? Mode + " · " + Detail : Mode;
}

public sealed partial record PlacementBar(int Place, int Count, double Fraction, string Share, bool Top)
{
    /// <summary>No game ended on this place: the row sinks back and shows no "0 · 0 %" (D3, A06).</summary>
    public bool Empty => Count == 0;

    /// <summary>The row's opacity: an empty place sinks back.</summary>
    public double Dim => Empty ? EmptyRowOpacity : 1;

    /// <summary>How far an empty place's row sinks back, here and on the Battlegrounds page.</summary>
    public const double EmptyRowOpacity = 0.4;
}

public sealed record HeroRow(string CardId, string Name, int Games, string AveragePlace, double AverageValue, string Top4, string Wins, string Net, int NetValue, string Tier);

public sealed record MetaHeroRow(
    int Rank,
    string CardId,
    string Name,
    string Tier,
    string AveragePlace,
    double AverageValue,
    string PickRate,
    string DataPoints,
    string Personal,
    double BarFraction,
    string Top4 = "",
    string FirstPlace = "",
    double Top4Value = 0,
    double FirstValue = 0,
    double PickValue = 0,
    int Games = 0);

public sealed record MetaCompRow(
    int Rank,
    string Name,
    string Tribes,
    string Power,
    string Difficulty,
    string Placement,
    double BarFraction,
    string Commit,
    string KeyCards);

/// <summary>A deck in "Your decks": a family of versions or a single one (<see cref="Key"/> is the family key).</summary>
public sealed record DeckRow(
    string Key,
    string Name,
    string Class,
    int Games,
    string Record,
    string WinRate,
    double WinFraction,
    string LastPlayed,
    string? DeckCode,
#if HEARTHPULSE_WPF
    string ClassKey = "",
#endif
    string Meta = "",
    string Versions = "")
{
    /// <summary>Too few games to tell: the win bar is faded.</summary>
    public bool Few => Games < Hearthpulse.Core.Stats.DeckDetails.SmallSample;
}

public sealed record MatchupRow(string Class, int Games, string Record, string WinRate, double WinFraction);

public sealed record StatTile(string Label, string Value, string Detail, int Sign = 0);

/// <summary>One trinket in the community tier list.</summary>
public sealed record MetaTrinketRow(int Rank, string CardId, string Name, string Tier, string Placement, string PickRate, string DataPoints, double BarFraction);

/// <summary>One minion or tavern spell in the community card list (compare cards of the same tier).</summary>
public sealed record MetaCardRow(int Rank, string CardId, string Name, string Kind, string Tier, string Placement, string Games, double BarFraction);

/// <summary>One MMR bracket the community stats have ("All", "Top 50%", "20%"...), for the tier lists' picker.</summary>
public sealed record BracketOption(int Value, string Label, bool IsSelected, RelayCommand Select);
