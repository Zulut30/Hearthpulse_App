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
/// <param name="Caption">"ArenaSmith", "Best pick", or the 7+ wins share of a class.</param>
public sealed record ArenaPickBadge(string CardId, string Number, string Tier, string Caption, bool IsBest);

/// <summary>One bar of the draft's mana curve.</summary>
public sealed record ArenaCurveBar(string Label, int Count, double Height);

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

    /// <summary>"Hearthpulse data · ArenaSmith score".</summary>
    public string ArenaSource => Loc.F("Arena.Source", ArenaMeta.Attribution);

    private void UpdateArena(ArenaDraftView? draft, AppSettings settings)
    {
        var shown = settings.OverlayEnabled && settings.ShowArenaDraft && draft is not null;
        ShowArenaDraft = shown;
        ShowArenaPicks = shown && draft!.Options.Count > 0;
        if (!shown)
        {
            _arenaPrint = null;
            return;
        }

        var scale = Math.Clamp(settings.OverlayScale, 0.6, 1.6);
        ArenaPickRowWidth = (draft!.HeroPick ? ArenaHeroPitch : ArenaCardPitch) * Math.Max(draft.Options.Count, 1) / scale;
        ArenaPickDrop = draft.HeroPick ? ArenaHeroDrop / scale : 0;

        // Snapshots arrive every 100 ms while anything changes; rebuild only when what the panels show changed.
        var print = $"{Loc.Instance.Language}|{draft.PlayerClass}|{draft.HasData}|{draft.Score.Average:0.00}|"
                    + string.Join(",", draft.Options.Select(o => $"{o.CardId}:{o.Score}:{o.WinRate}:{o.IsBest}")) + "|"
                    + string.Join(",", draft.Deck.Select(c => $"{c.CardId}:{c.Count}:{c.Score}")) + "|"
                    + string.Join(",", draft.Classes.Select(c => $"{c.PlayerClass}:{c.WinRate}:{c.Offered}"));
        if (print == _arenaPrint)
        {
            return;
        }

        _arenaPrint = print;
        var culture = CultureInfo.CurrentCulture;
        ArenaPicks = draft.Options.Select(o => draft.HeroPick
                ? new ArenaPickBadge(o.CardId,
                    o.WinRate?.ToString("P1", culture) ?? "—",
                    o.Tier ?? string.Empty,
                    o.IsBest ? Loc.T("Arena.BestClass")
                        : draft.Classes.FirstOrDefault(c => c.PlayerClass == o.PlayerClass)?.SevenPlus is { } seven ? Loc.F("Arena.SevenPlus", seven.ToString("P0", culture))
                        : ClassName(o.PlayerClass),
                    o.IsBest)
                : new ArenaPickBadge(o.CardId,
                    o.Score?.ToString("0", culture) ?? "—",
                    o.Tier ?? string.Empty,
                    o.IsBest ? Loc.T("Arena.BestPick") : o.Score is null ? Loc.T("Arena.NoScore") : Loc.T("Arena.Score"),
                    o.IsBest))
            .ToList();

        ArenaDraftTitle = draft.PlayerClass is { } cls ? ClassName(cls) : Loc.T("Arena.ChooseHero");
        ArenaDraftPicks = $"{draft.Picks}/{ArenaDraft.DeckSize}";
        ArenaAverage = draft.Score.Average?.ToString("0.0", culture) ?? "—";
        ArenaHasPicks = draft.Picks > 0;
        ArenaAverageTier = draft.Score.Tier ?? string.Empty;
        var most = Math.Max(1, draft.Score.Curve.Max());
        ArenaCurve = draft.Score.Curve.Select((count, cost) => new ArenaCurveBar(
                cost == ArenaDraft.CurveBuckets - 1 ? $"{cost}+" : cost.ToString(culture),
                count,
                count == 0 ? 2 : Math.Max(4, ArenaCurveHeight * count / most)))
            .ToList();
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
        ArenaNoData = draft.HasData ? string.Empty : Loc.T("Arena.NoData");
        Raise(nameof(ArenaSource));
    }
}
