using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The bar over one offered card: the guide's call, a gauge from "replace" to "keep" with the edge in points, a small
/// second line (keep rate, win rate when drawn) and flags; details in the tooltip.
/// </summary>
/// <param name="Call">"Keep", "Replace" or "No data": what the guide recommends.</param>
/// <param name="Number">What keeping is worth against replacing, in points ("+3.1"); empty without numbers.</param>
/// <param name="Good">The edge is at least <see cref="MulliganGuide.CloseCall"/>; <paramref name="Bad"/> the same below 0.</param>
/// <param name="FillLeft">Width of the gauge's fill left of its middle (a card to replace), in overlay units.</param>
/// <param name="FillRight">Width of the fill right of the middle (a card to keep).</param>
/// <param name="Details">"Kept 61% · drawn −1.3": the keep rate and the win rate when drawn, against the deck's.</param>
/// <param name="Kept">The player's current choice (starts as the recommendation; a click toggles it).</param>
/// <param name="Duplicate">A second copy (counts once, heuristic).</param>
/// <param name="Late">Usually played late (curve rule).</param>
public sealed record MulliganPlate(
    int EntityId,
    string Name,
    string Call,
    bool KeepCall,
    bool MulliganCall,
    string Number,
    bool Good,
    bool Bad,
    double FillLeft,
    double FillRight,
    string Details,
    bool Synergy,
    bool Duplicate,
    bool Late,
    bool Kept,
    string Tooltip)
{
    /// <summary>Neither keep nor replace: no numbers for the card.</summary>
    public bool NoCall => !KeepCall && !MulliganCall;

    /// <summary>A close call: the edge is within half a point either way (drawn neutral).</summary>
    public bool Close => !NoCall && !Good && !Bad;
}

/// <summary>
/// The mulligan bars (MulliganPanel.xaml): a bar over each offered card, lined up with the cards of the mulligan screen,
/// under one summary line. It is up while the local MULLIGAN_STATE is INPUT or DEALING (gone at WAITING), with or without numbers.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Width of the row of offered cards on the mulligan screen, in units of a 1080-high screen: 3 cards 340 apart, 4 cards 255.</summary>
    public const double MulliganRowGameWidth = 1020;

    /// <summary>Half the gauge's track, in overlay units (MulliganPanel's track is twice this).</summary>
    public const double MulliganGaugeHalf = 38;

    /// <summary>An edge of this many win rate points fills half the gauge.</summary>
    private const double MulliganGaugeFull = 0.04;

    /// <summary>
    /// From this overlay scale the bars drop their second line (it stays in the tooltip): taller bars would reach the
    /// opponent's hand markers at the top of the mulligan screen.
    /// </summary>
    private const double MulliganCompactScale = 1.4;

    private bool _mulliganCompact;

    private double _mulliganRowWidth = MulliganRowGameWidth;

    private bool _showMulligan;
    private IReadOnlyList<MulliganPlate> _mulliganCards = [];
    private string _mulliganSummary = string.Empty;
    private string _mulliganSource = string.Empty;
    private string _mulliganDetails = string.Empty;
    private bool _mulliganImpactGood;
    private bool _mulliganImpactBad;
    private bool _mulliganHasWarnings;
    private MulliganView? _mulligan;
    private string? _mulliganHand;
    private string? _mulliganPrint;
    private bool _mulliganTouched;
    private bool _mulliganOffNoted;
    private readonly HashSet<int> _mulliganKept = new();
    private RelayCommand? _toggleMulliganCard;

    public bool ShowMulligan { get => _showMulligan; private set => Set(ref _showMulligan, value); }

    /// <summary>
    /// The strip's row in overlay units: the game's card row, whatever the player's overlay scale (which only sizes the
    /// badges), so each badge stays under its card.
    /// </summary>
    public double MulliganRowWidth { get => _mulliganRowWidth; private set => Set(ref _mulliganRowWidth, value); }

    public IReadOnlyList<MulliganPlate> MulliganCards { get => _mulliganCards; private set => Set(ref _mulliganCards, value); }

    /// <summary>"Expected impact of your keeps: +2.1%", or why there are no numbers.</summary>
    public string MulliganSummary { get => _mulliganSummary; private set => Set(ref _mulliganSummary, value); }

    /// <summary>"Egg Death Knight · HSReplay Legend EU · on the coin" (small, under the summary).</summary>
    public string MulliganSource { get => _mulliganSource; private set => Set(ref _mulliganSource, value); }

    /// <summary>The summary's tooltip: average hand, full mulligan, recommended keeps, curve and synergy notes.</summary>
    public string MulliganDetails { get => _mulliganDetails; private set => Set(ref _mulliganDetails, value); }

    public bool MulliganImpactGood { get => _mulliganImpactGood; private set => Set(ref _mulliganImpactGood, value); }

    public bool MulliganImpactBad { get => _mulliganImpactBad; private set => Set(ref _mulliganImpactBad, value); }

    /// <summary>A curve or synergy remark is in <see cref="MulliganDetails"/> (an info mark next to the summary).</summary>
    public bool MulliganHasWarnings { get => _mulliganHasWarnings; private set => Set(ref _mulliganHasWarnings, value); }

    /// <summary>Click a badge: mark the card as kept or replaced; the summary follows.</summary>
    public RelayCommand ToggleMulliganCard => _toggleMulliganCard ??= new RelayCommand(p =>
    {
        if (p is int id && !_mulliganKept.Remove(id))
        {
            _mulliganKept.Add(id);
        }

        _mulliganTouched = true;

        RebuildMulligan();
    });

    private void UpdateMulligan(MulliganView? mulligan, AppSettings settings)
    {
        ShowMulligan = settings.ShowMulligan && mulligan is not null;
        MulliganRowWidth = MulliganRowGameWidth / Math.Clamp(settings.OverlayScale, 0.6, 1.6);
        _mulliganCompact = settings.OverlayScale >= MulliganCompactScale;
        // The tracker logs why the strip has or lacks numbers; only the setting is known here.
        var offBySetting = mulligan is not null && !settings.ShowMulligan;
        if (offBySetting && !_mulliganOffNoted)
        {
            Diag.Info("Mulligan panel hidden: \"Mulligan stats over your opening hand\" is off in Settings");
        }

        _mulliganOffNoted = offBySetting;
        _mulligan = mulligan;
        if (mulligan is null)
        {
            _mulliganHand = null;
            _mulliganPrint = null;
            return;
        }

        // A new hand starts from the recommendation. The player's clicks last for the whole hand, also when the numbers
        // arrive from another source mid-mulligan; untouched keeps follow the newest recommendation.
        var hand = string.Join(",", mulligan.Cards.Select(c => $"{c.EntityId}:{c.CardId}"));
        if (hand != _mulliganHand)
        {
            _mulliganHand = hand;
            _mulliganTouched = false;
        }

        // Without numbers there is no advice: every card starts kept, so no badge looks like "replace it".
        var recommended = mulligan.NoStats is null ? mulligan.Recommended : mulligan.Cards.Select(c => c.EntityId).ToList();
        if (!_mulliganTouched && !_mulliganKept.SetEquals(recommended))
        {
            _mulliganKept.Clear();
            _mulliganKept.UnionWith(recommended);
        }

        RebuildMulligan();
    }

    private void RebuildMulligan()
    {
        if (_mulligan is not { } view)
        {
            return;
        }

        // Snapshots arrive every 100 ms while anything changes; rebuild the badges only when what they show changed. The
        // language is part of it: after a switch the host pushes the last snapshot again and the texts are made anew.
        var print = $"{Loc.Instance.Language}|{view.Context}|{view.Source}|{view.NoStats}|{view.BaseWinRate:0.0000}|{view.ReplaceDelta:0.0000}|{view.OpponentClass}|{view.ClassShift:0.0000}|{view.GoingFirst}|{view.SliceRank}|{_mulliganCompact}|"
                    + $"{string.Join(",", _mulliganKept.Order())}|"
                    + string.Join(";", view.Cards.Select(c => $"{c.EntityId}|{c.CardId}|{c.Call}|{c.Delta:0.0000}|{c.Edge:0.0000}|{c.KeepRate:0.0000}|{c.KeptDelta:0.0000}|{c.DrawnDelta:0.0000}"));
        if (print == _mulliganPrint)
        {
            return;
        }

        _mulliganPrint = print;
        var culture = CultureInfo.CurrentCulture;
        var initiative = view.GoingFirst switch { true => Loc.T("Mulligan.GoingFirst"), false => Loc.T("Mulligan.OnTheCoin"), null => null };
        if (view.NoStats is { } missing)
        {
            MulliganSummary = missing;
            MulliganSource = initiative ?? string.Empty;
            MulliganDetails = Loc.T("Mulligan.NoNumbers");
            MulliganImpactGood = MulliganImpactBad = MulliganHasWarnings = false;
        }
        else
        {
            var impact = MulliganGuide.Evaluate(view, _mulliganKept);
            var vsAverage = impact.Expected - view.BaseWinRate;
            var vsClass = view.OpponentClass is { } opponent ? Loc.F("Mulligan.VsClass", ClassName(opponent)) : string.Empty;
            MulliganSummary = Loc.F("Mulligan.Impact", OverlayFormat.SignedPercent(vsAverage));
            // Archetype names come from the API in English.
            var context = string.IsNullOrEmpty(view.Context) ? view.Context : ArchetypeTranslations.Current.Display(view.Context);
            // A source that already names the rank ("Hearthpulse data, Legend, 7 days") is not followed by "slice: Legend".
            var slices = view.SliceRank is { } rank && view.Source?.Contains(SliceRankName(rank), StringComparison.CurrentCultureIgnoreCase) != true
                ? Loc.F("Mulligan.SliceSource", SliceRankName(rank))
                : null;
            MulliganSource = string.Join(" · ", new[] { context, view.Source, slices, initiative }.Where(s => !string.IsNullOrEmpty(s)));
            var notes = impact.Warnings
                .Concat(impact.Synergies.Select(s => Loc.F("Mulligan.SynergyKept", s.NameA, s.NameB, Points(s.Interaction))))
                .ToList();
            MulliganDetails = string.Join("\n", new[]
            {
                Loc.F("Mulligan.AverageHand", vsClass + (initiative is null ? string.Empty : " " + initiative), view.BaseWinRate.ToString("P1", culture)),
                Loc.F("Mulligan.YourKeeps", impact.Kept, view.Cards.Count, impact.Expected.ToString("P1", culture)),
                Loc.F("Mulligan.FullMulligan", impact.FullMulligan.ToString("P1", culture), impact.Recommended.ToString("P1", culture)),
            }.Concat(notes).Append(view.HasPairData ? string.Empty : Loc.T("Mulligan.NoPairData"))
             .Where(s => s.Length > 0));
            MulliganImpactGood = impact.Expected >= impact.Recommended - 0.0005 && vsAverage > 0.0005;
            MulliganImpactBad = impact.Expected < impact.Recommended - 0.005;
            MulliganHasWarnings = notes.Count > 0;
        }

        MulliganCards = view.Cards.Select(c =>
        {
            var kept = _mulliganKept.Contains(c.EntityId);
            var synergy = view.Synergies.FirstOrDefault(s => s.EntityA == c.EntityId || s.EntityB == c.EntityId);
            var late = MulliganGuide.IsLate(c);
            var edge = c.Edge ?? 0;
            var fill = c.Edge is null ? 0 : Math.Min(1, Math.Abs(edge) / MulliganGaugeFull) * MulliganGaugeHalf;
            var details = _mulliganCompact ? string.Empty : string.Join(" · ", new[]
            {
                c.KeepRate is { } keepRate ? Loc.F("Mulligan.KeepRateShort", keepRate.ToString("P0", culture)) : null,
                c.DrawnDelta is { } drawn ? Loc.F("Mulligan.DrawnShort", Points(drawn)) : null,
            }.Where(s => s is not null));
            var lines = new List<string> { c.Name };
            if (c.WinRate is { } rate && c.Delta is { } delta)
            {
                if (c.Edge is not null)
                {
                    lines.Add(Loc.F("Mulligan.EdgeLine", Points(edge), Points(delta), Points(view.ReplaceDelta)));
                    if (Math.Abs(edge - (delta - view.ReplaceDelta)) > 0.0005)
                    {
                        // A kept synergy partner or the other copy changes what keeping this one adds.
                        lines.Add(Loc.T("Mulligan.EdgeWithOthers"));
                    }
                }

                lines.Add(Loc.P("Mulligan.CardWinRate", c.Sample, rate.ToString("P1", culture), view.CardBaseline.ToString("P1", culture)));
                lines.Add(Loc.F("Mulligan.KeptBy", c.KeepRate?.ToString("P0", culture), c.ExpectedTurn.ToString("0.#", culture)));
                if (c.CoinShift is { } coinShift)
                {
                    lines.Add(Loc.F(view.GoingFirst == true ? "Mulligan.Shift.First" : "Mulligan.Shift.Coin", Points(coinShift)));
                }

                if (c.OpponentShift is { } opponentShift)
                {
                    lines.Add(Loc.F("Mulligan.Shift.Class", ClassName(view.OpponentClass), Points(opponentShift)));
                }
            }
            else
            {
                lines.Add(Loc.T("Mulligan.NotEnoughGames"));
            }

            if (c.KeptDelta is not null || c.ReplacedDelta is not null)
            {
                lines.Add(Loc.F("Mulligan.KeptReplaced", c.KeptDelta is { } k ? Points(k) : "—", c.ReplacedDelta is { } r ? Points(r) : "—", SliceName(c.Slice)));
            }

            if (c.DrawnDelta is { } drawnDelta)
            {
                lines.Add(Loc.F("Mulligan.DrawnLine", Points(drawnDelta)));
            }

            if (c.Copy > 1)
            {
                lines.Add(Loc.T("Mulligan.SecondCopy"));
            }

            if (synergy is not null)
            {
                lines.Add(Loc.F("Mulligan.SynergyWith", synergy.EntityA == c.EntityId ? synergy.NameB : synergy.NameA, Points(synergy.Interaction)));
            }

            if (late)
            {
                lines.Add(Loc.F("Mulligan.LateCard", c.ExpectedTurn.ToString("0", culture)));
            }

            lines.Add(kept ? Loc.T("Mulligan.Keeping") : Loc.T("Mulligan.Replacing"));
            return new MulliganPlate(
                c.EntityId,
                c.Name,
                c.Call switch { Core.Tracking.MulliganCall.Keep => Loc.T("Mulligan.Call.Keep"), Core.Tracking.MulliganCall.Mulligan => Loc.T("Mulligan.Call.Replace"), _ => Loc.T("Mulligan.Call.NoData") },
                c.Call == Core.Tracking.MulliganCall.Keep,
                c.Call == Core.Tracking.MulliganCall.Mulligan,
                c.Edge is null ? string.Empty : Points(edge),
                c.Edge >= MulliganGuide.CloseCall,
                c.Edge <= -MulliganGuide.CloseCall,
                edge < 0 ? fill : 0,
                edge > 0 ? fill : 0,
                details,
                synergy is not null,
                c.Copy > 1,
                late,
                kept,
                string.Join("\n", lines));
        }).ToList();
    }

    private static string SliceRankName(string rank) => rank == "legend" ? Loc.T("Mulligan.Slice.Legend") : Loc.T("Mulligan.Slice.AllRanks");

    /// <summary>"slice: Legend, on the coin": where a card's kept / replaced numbers come from.</summary>
    private static string SliceName(MulliganSlice? slice) => slice is null
        ? string.Empty
        : string.Join(", ", new[]
        {
            Loc.F("Mulligan.SliceSource", SliceRankName(slice.Rank)),
            slice.Coin switch { "going_first" => Loc.T("Mulligan.GoingFirst"), "on_coin" => Loc.T("Mulligan.OnTheCoin"), _ => null },
            slice.OpponentClass == MulliganGuide.AnyOpponent ? null : Loc.F("Mulligan.VsClass", ClassName(slice.OpponentClass)).Trim(),
        }.Where(s => s is not null));

    /// <summary>A win rate difference in points: 0.023 → "+2.3".</summary>
    private static string Points(double delta) => (delta * 100).ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);
}
