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
    string Key = "") : IImmutableView
{
    /// <summary>The mode and the game's details on one line: "Battlegrounds · Tier 5 · 11 turns".</summary>
    public string Summary => Detail.Length > 0 ? Mode + " · " + Detail : Mode;
}

public sealed record HeroRow(string CardId, string Name, int Games, string AveragePlace, double AverageValue, string Top4, string Wins, string Net, int NetValue, string Tier) : IImmutableView;

/// <summary>
/// One hero of the Battlegrounds tier list. <see cref="Rank"/> is its place in the service's order (sorting keeps it);
/// <see cref="Top"/> is the top-4 share, or the top-2 share of a Duos list; a number the service lacks is "—".
/// </summary>
public sealed record MetaHeroRow(
    int Rank,
    string CardId,
    string Name,
    string Tier,
    string AveragePlace,
    double BarFraction,
    string Top,
    string FirstPlace,
    string PickRate,
    string Personal) : IImmutableView;

/// <summary>
/// One comp of the Comps tab, or one tribe of its stand-in list: the name, a line on its right (the difficulty, or the
/// tribe's placement and games), the core cards with their prefix and the tip; an empty text is not shown.
/// </summary>
public sealed record MetaCompRow(string Name, string Side, string Core, string Tip) : IImmutableView;

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
    string Versions = "") : IImmutableView
{
    /// <summary>Too few games to tell: the win bar is faded.</summary>
    public bool Few => Games < Hearthpulse.Core.Stats.DeckDetails.SmallSample;
}

public sealed record StatTile(string Label, string Value, string Detail, int Sign = 0) : IImmutableView;

/// <summary>One trinket of the tier list; <see cref="Detail"/> is its size and cost ("Lesser · cost 2").</summary>
public sealed record MetaTrinketRow(int Rank, string CardId, string Name, string Detail, string Tier, string Placement, double BarFraction, string PickRate) : IImmutableView;

/// <summary>
/// One minion or tavern spell of the card list (compare cards of the same tavern tier). <see cref="Impact"/> is signed
/// ("+0.55"); <see cref="ImpactSign"/> colours it and is 0 on a thin sample; <see cref="ShowsStats"/> is the list's
/// own flag (the three stat columns show only when a row of the list has numbers), repeated on every row.
/// </summary>
public sealed record MetaCardRow(
    int Rank,
    string CardId,
    string Name,
    string Detail,
    string Impact,
    int ImpactSign,
    string CombatWinRate,
    string PlacementSplit,
    string Popularity,
    bool ShowsStats) : IImmutableView;

/// <summary>One MMR slice a tier list has ("Top 50%", "20%"...), for the tabs' picker; <see cref="Value"/> is the percent.</summary>
public sealed record BracketOption(int Value, string Label, bool IsSelected, RelayCommand Select) : IImmutableView;
