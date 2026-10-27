// The draft-screen geometry below (ArenaCardPitch, ArenaHeroPitch, ArenaHeroDrop and the panel margin in the overlay XAML)
// is adapted from HdtArenaHelper's ArenaOverlayWindow (live-tuned anchors for the Arena draft screen):
//
// MIT License
// Copyright (c) 2026 Alessandro Colace
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
// persons to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>The badge over one offered draft card (or hero): tier, score (or class win rate), a caption.</summary>
/// <param name="Number">ArenaSmith score ("54") or class win rate ("55.4%"); "—" without numbers.</param>
/// <param name="Tier">Tier letter for the colour, or empty.</param>
/// <param name="Caption">"Card score", "Best pick", or the class's place and 7+ wins share.</param>
/// <param name="FitCost">Where the card sits on the curve: "3 mana · 4 in deck"; empty for a hero.</param>
/// <param name="FitType">Its type in the deck ("Spell · 5 in deck") or the package it brings ("+2 cards"); empty for a hero.</param>
public sealed record ArenaPickBadge(string CardId, string Number, string Tier, string Caption, bool IsBest, string FitCost = "", string FitType = "");

/// <summary>One bar of the draft's mana curve.</summary>
/// <param name="Offered">An offered card costs this much: the bar is highlighted.</param>
public sealed record ArenaCurveBar(string Label, int Count, double Height, bool Offered = false);

/// <summary>One distinct card of the deck so far.</summary>
public sealed record ArenaDeckRow(string CardId, string Cost, string Name, string Copies, string Score, string Tier);

/// <summary>A class in the hero-pick list: its community win rate and 7+ share.</summary>
public sealed record ArenaClassRow(string Name, string WinRate, string SevenPlus, string Tier, bool Offered);

/// <summary>
/// The Arena draft helper (ArenaPicksPanel, ArenaDraftPanel): a badge over each offered card with its ArenaSmith score
/// (class win rate at the hero pick), the best one marked, and a panel with the deck so far, its curve and average.
/// Up while the draft screen is open; nothing on it takes clicks, so the cards stay clickable.
/// </summary>
public sealed partial class OverlayViewModel
{
    // HdtArenaHelper lays the offer out on the 4:3 safe area (1440 wide on a 1080-high screen), centred on its left
    // 3.25/4.25 (the client's deck list owns the rest): cards 0.26 of that width apart, heroes 0.29, heroes 0.05 of the
    // height lower. The panel's default margin in the XAML (right 339 = twice the left shift of that centre) comes from it too.
    private const double ArenaPlayWidth = 1080 * 4.0 / 3 * 3.25 / 4.25;

    /// <summary>Distance between two offered cards on the draft screen, in units of a 1080-high screen.</summary>
    public const double ArenaCardPitch = ArenaPlayWidth * 0.26;

    /// <summary>Distance between two offered heroes.</summary>
    public const double ArenaHeroPitch = ArenaPlayWidth * 0.29;

    /// <summary>How much lower the hero badges sit than the card badges.</summary>
    public const double ArenaHeroDrop = 1080 * 0.05;

    private const double ArenaCurveHeight = 34;

    private bool _showArenaPicks;
    private bool _showArenaDraft;
    private IReadOnlyList<ArenaPickBadge> _arenaPicks = [];
    private double _arenaPickRowWidth = ArenaCardPitch * 3;
    private double _arenaPickDrop;
    private string _arenaDraftTitle = string.Empty;
    private string _arenaDraftPicks = string.Empty;
    private string _arenaAverage = "—";
    private string _arenaAverageTier = string.Empty;
    private IReadOnlyList<ArenaCurveBar> _arenaCurve = [];
    private IReadOnlyList<ArenaDeckRow> _arenaDeck = [];
    private IReadOnlyList<ArenaClassRow> _arenaClasses = [];
    private string _arenaNoData = string.Empty;
    private string _arenaMode = string.Empty;
    private string _arenaTypes = string.Empty;
    private string _arenaSliceNote = string.Empty;
    private string _arenaClassLine = string.Empty;
    private bool _arenaHasPicks;
    private string? _arenaPrint;

    public bool ShowArenaPicks { get => _showArenaPicks; private set => Set(ref _showArenaPicks, value); }

    public bool ShowArenaDraft { get => _showArenaDraft; private set => Set(ref _showArenaDraft, value); }

    public IReadOnlyList<ArenaPickBadge> ArenaPicks { get => _arenaPicks; private set => Set(ref _arenaPicks, value); }

    /// <summary>
    /// The badge row in overlay units: the game's card row whatever the player's overlay scale (which only sizes the
    /// badges), so each badge stays over its card. Wider for heroes.
    /// </summary>
    public double ArenaPickRowWidth { get => _arenaPickRowWidth; private set => Set(ref _arenaPickRowWidth, value); }

    /// <summary>Space above the badges: the hero portraits sit lower than the cards.</summary>
    public double ArenaPickDrop { get => _arenaPickDrop; private set => Set(ref _arenaPickDrop, value); }

    /// <summary>The run's class, or "Choose a hero".</summary>
    public string ArenaDraftTitle { get => _arenaDraftTitle; private set => Set(ref _arenaDraftTitle, value); }

    /// <summary>"12/30".</summary>
    public string ArenaDraftPicks { get => _arenaDraftPicks; private set => Set(ref _arenaDraftPicks, value); }

    public string ArenaAverage { get => _arenaAverage; private set => Set(ref _arenaAverage, value); }

    /// <summary>At least one card is picked: the average and the curve are shown.</summary>
    public bool ArenaHasPicks { get => _arenaHasPicks; private set => Set(ref _arenaHasPicks, value); }

    public string ArenaAverageTier { get => _arenaAverageTier; private set => Set(ref _arenaAverageTier, value); }

    public IReadOnlyList<ArenaCurveBar> ArenaCurve { get => _arenaCurve; private set => Set(ref _arenaCurve, value); }

    public IReadOnlyList<ArenaDeckRow> ArenaDeck { get => _arenaDeck; private set => Set(ref _arenaDeck, value); }

    /// <summary>Classes with community win rates; shown at the hero pick.</summary>
    public IReadOnlyList<ArenaClassRow> ArenaClasses { get => _arenaClasses; private set => Set(ref _arenaClasses, value); }

    /// <summary>Why there are no numbers (server and cache empty), else empty.</summary>
    public string ArenaNoData { get => _arenaNoData; private set => Set(ref _arenaNoData, value); }

    /// <summary>"Underground Arena" or "Normal Arena" once the client told the run's mode, else empty.</summary>
    public string ArenaMode { get => _arenaMode; private set => Set(ref _arenaMode, value); }

    /// <summary>Card types of the deck so far: "12 minions · 5 spells · 1 weapon"; empty before the first card.</summary>
    public string ArenaTypes { get => _arenaTypes; private set => Set(ref _arenaTypes, value); }

    /// <summary>"Scores come from Underground Arena games" when the run is the other mode, else empty.</summary>
    public string ArenaSliceNote { get => _arenaSliceNote; private set => Set(ref _arenaSliceNote, value); }

    /// <summary>The run's class results after the hero pick: "Win rate 55.2% · #1 of 11 classes · 7+ wins: 15%"; else empty.</summary>
    public string ArenaClassLine { get => _arenaClassLine; private set => Set(ref _arenaClassLine, value); }

    /// <summary>"Hearthpulse data · ArenaSmith score".</summary>
    public string ArenaSource => Loc.F("Arena.Source", ArenaMeta.Attribution);

    private void UpdateArena(ArenaDraftView? draft, AppSettings settings)
    {
        var shown = settings.OverlayEnabled && settings.ShowArenaDraft && draft is not null;
        ShowArenaDraft = shown;
        // Hero powers have no numbers: their pick shows only the panel.
        ShowArenaPicks = shown && draft!.Options.Count > 0 && draft.Phase is ArenaDraftPhase.Hero or ArenaDraftPhase.Cards;
        if (!shown)
        {
            _arenaPrint = null;
            return;
        }

        var scale = Math.Clamp(settings.OverlayScale, 0.6, 1.6);
        ArenaPickRowWidth = (draft!.HeroPick ? ArenaHeroPitch : ArenaCardPitch) * Math.Max(draft.Options.Count, 1) / scale;
        ArenaPickDrop = draft.HeroPick ? ArenaHeroDrop / scale : 0;

        // Snapshots arrive every 100 ms while anything changes; rebuild only when what the panels show changed.
        var print = $"{Loc.Instance.Language}|{draft.PlayerClass}|{draft.HasData}|{draft.Score.Average:0.00}|{draft.Phase}|{draft.Underground}|{draft.ScoresFromOtherMode}|"
                    + string.Join(",", draft.Options.Select(o => $"{o.CardId}:{o.Score}:{o.WinRate}:{o.IsBest}:{o.SameCost}:{o.SameType}:{o.Package?.Count}")) + "|"
                    + string.Join(",", draft.Deck.Select(c => $"{c.CardId}:{c.Count}:{c.Score}")) + "|"
                    + string.Join(",", draft.Classes.Select(c => $"{c.PlayerClass}:{c.WinRate}:{c.Offered}")) + "|"
                    + $"{draft.RunClass?.WinRate}:{draft.RunClass?.SevenPlus}:{draft.RunClass?.Rank}";
        if (print == _arenaPrint)
        {
            return;
        }

        _arenaPrint = print;
        var culture = CultureInfo.CurrentCulture;
        ArenaPicks = draft.Phase is ArenaDraftPhase.Hero or ArenaDraftPhase.Cards
            ? draft.Options.Select(o => draft.HeroPick ? HeroBadge(o, draft, culture) : CardBadge(o, culture)).ToList()
            : [];

        // The hero power is picked after the hero, so the class is known by then: the pick still gets its own title.
        ArenaDraftTitle = draft.Phase == ArenaDraftPhase.HeroPower ? Loc.T("Arena.ChooseHeroPower")
            : draft.PlayerClass is { } cls ? ClassName(cls)
            : Loc.T("Arena.ChooseHero");
        ArenaDraftPicks = $"{draft.Picks}/{ArenaDraft.DeckSize}";
        ArenaMode = draft.Underground switch
        {
            true => Loc.T("Arena.Mode.Underground"),
            false => Loc.T("Arena.Mode.Normal"),
            null => string.Empty,
        };
        ArenaSliceNote = draft.ScoresFromOtherMode
            ? Loc.T(draft.Underground == true ? "Arena.Slice.Normal" : "Arena.Slice.Underground")
            : string.Empty;
        ArenaAverage = draft.Score.Average?.ToString("0.0", culture) ?? "—";
        ArenaHasPicks = draft.Picks > 0;
        ArenaAverageTier = draft.Score.Tier ?? string.Empty;
        var most = Math.Max(1, draft.Score.Curve.Max());
        var offeredCosts = draft.Phase == ArenaDraftPhase.Cards
            ? draft.Options.Where(o => o.Cost is not null).Select(o => ArenaDraft.Bucket(o.Cost!.Value)).ToHashSet()
            : [];
        ArenaCurve = draft.Score.Curve.Select((count, cost) => new ArenaCurveBar(
                cost == ArenaDraft.CurveBuckets - 1 ? $"{cost}+" : cost.ToString(culture),
                count,
                count == 0 ? 2 : Math.Max(4, ArenaCurveHeight * count / most),
                offeredCosts.Contains(cost)))
            .ToList();
        ArenaTypes = TypesLine(draft.Types ?? ArenaDeckTypes.Empty);
        ArenaDeck = draft.Deck.Select(c => new ArenaDeckRow(
                c.CardId,
                c.Cost.ToString(culture),
                c.Name,
                c.Count > 1 ? "×" + c.Count.ToString(culture) : string.Empty,
                c.Score?.ToString("0", culture) ?? "—",
                c.Tier ?? string.Empty))
            .ToList();
        ArenaClasses = draft.Classes.Select(c => new ArenaClassRow(
                ClassName(c.PlayerClass),
                c.WinRate.ToString("P1", culture),
                c.SevenPlus is { } seven ? Loc.F("Arena.SevenPlusShort", seven.ToString("P0", culture)) : string.Empty,
                ArenaDraft.TierOfWinRate(c.WinRate) ?? string.Empty,
                c.Offered))
            .ToList();
        ArenaClassLine = draft.RunClass is { } run
            ? string.Join(" · ", new[]
            {
                Loc.F("Arena.RunClass.WinRate", run.WinRate.ToString("P1", culture)),
                Loc.F("Arena.ClassRank", run.Rank, run.Of),
                run.SevenPlus is { } runSeven ? Loc.F("Arena.SevenPlus", runSeven.ToString("P0", culture)) : null,
            }.Where(part => part is not null))
            : string.Empty;
        ArenaNoData = draft.HasData ? string.Empty : Loc.T("Arena.NoData");
        Raise(nameof(ArenaSource));
    }

    /// <summary>A hero: class win rate, its place among the classes and the 7+ wins share.</summary>
    private static ArenaPickBadge HeroBadge(ArenaPickOption o, ArenaDraftView draft, CultureInfo culture)
    {
        var seven = draft.Classes.FirstOrDefault(c => c.PlayerClass == o.PlayerClass)?.SevenPlus;
        var caption = o.IsBest ? Loc.T("Arena.BestClass")
            : seven is { } s ? Loc.F("Arena.SevenPlus", s.ToString("P0", culture))
            : ClassName(o.PlayerClass);
        var rank = o.ClassRank is { } place && draft.Classes.Count > 0 ? Loc.F("Arena.ClassRank", place, draft.Classes.Count) : string.Empty;
        var detail = o.IsBest && seven is { } best ? Loc.F("Arena.SevenPlusShort", best.ToString("P0", culture)) : string.Empty;
        return new ArenaPickBadge(o.CardId, o.WinRate?.ToString("P1", culture) ?? "—", o.Tier ?? string.Empty, caption, o.IsBest, rank, detail);
    }

    /// <summary>A card: its score, and where it would sit in the deck (curve bucket and type, or its package).</summary>
    private static ArenaPickBadge CardBadge(ArenaPickOption o, CultureInfo culture)
    {
        var caption = o.IsBest ? Loc.T("Arena.BestPick") : o.Score is null ? Loc.T("Arena.NoScore") : Loc.T("Arena.Score");
        var cost = o.Cost is { } c
            ? Loc.F("Arena.Fit", Loc.P("Arena.Mana", c), o.SameCost)
            : string.Empty;
        var type = o.Package is { Count: > 0 } package ? Loc.P("Arena.Package", package.Count)
            : o.CardType is { } kind && TypeName(kind) is { Length: > 0 } name ? Loc.F("Arena.Fit", name, o.SameType)
            : string.Empty;
        return new ArenaPickBadge(o.CardId, o.Score?.ToString("0", culture) ?? "—", o.Tier ?? string.Empty, caption, o.IsBest, cost, type);
    }

    /// <summary>"Minion", "Spell"... for the badge; empty for a type the draft does not offer.</summary>
    private static string TypeName(string cardType) => cardType switch
    {
        "MINION" or "SPELL" or "WEAPON" or "LOCATION" or "HERO" => Loc.T("Arena.Type." + cardType),
        _ => string.Empty,
    };

    /// <summary>"12 minions · 5 spells · 1 weapon": the types the deck has, empty before the first card.</summary>
    private static string TypesLine(ArenaDeckTypes types)
    {
        var parts = new List<string>(4);
        if (types.Minions > 0) parts.Add(Loc.P("Arena.Types.Minions", types.Minions));
        if (types.Spells > 0) parts.Add(Loc.P("Arena.Types.Spells", types.Spells));
        if (types.Weapons > 0) parts.Add(Loc.P("Arena.Types.Weapons", types.Weapons));
        if (types.Locations > 0) parts.Add(Loc.P("Arena.Types.Locations", types.Locations));
        return string.Join(" · ", parts);
    }
}
