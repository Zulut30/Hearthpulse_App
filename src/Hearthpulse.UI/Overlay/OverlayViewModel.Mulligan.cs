using System.Globalization;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.UI.Overlay;

/// <summary>The badge under one offered card: the guide's call, one number, small flags; details in the tooltip.</summary>
/// <param name="Call">"Keep", "Replace" or "No data": what the guide recommends.</param>
/// <param name="Number">The card's win rate delta in points ("+3.1"), else its win rate; empty without numbers.</param>
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
    bool Synergy,
    bool Duplicate,
    bool Late,
    bool Kept,
    string Tooltip)
{
    /// <summary>Neither keep nor replace: no numbers for the card.</summary>
    public bool NoCall => !KeepCall && !MulliganCall;
}

/// <summary>
/// The mulligan strip (MulliganPanel.axaml): a slim badge under each offered card, lined up with the cards of the
/// mulligan screen, and one summary line. It is up while the local MULLIGAN_STATE is INPUT or DEALING (gone at WAITING), with or without numbers.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Width of the row of offered cards on the mulligan screen, in units of a 1080-high screen: 3 cards 340 apart, 4 cards 255.</summary>
    public const double MulliganRowGameWidth = 1020;

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
        var print = $"{Loc.Instance.Language}|{view.Context}|{view.Source}|{view.NoStats}|{view.BaseWinRate:0.0000}|{view.ReplaceDelta:0.0000}|{view.OpponentClass}|{view.ClassShift:0.0000}|{view.GoingFirst}|"
                    + $"{string.Join(",", _mulliganKept.Order())}|"
                    + string.Join(";", view.Cards.Select(c => $"{c.EntityId}|{c.CardId}|{c.Call}|{c.Delta:0.0000}|{c.KeepRate:0.0000}"));
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
            MulliganSummary = Loc.F("Mulligan.Impact", Points(vsAverage));
            // Archetype names come from the API in English.
            var context = string.IsNullOrEmpty(view.Context) ? view.Context : ArchetypeTranslations.Current.Display(view.Context);
            MulliganSource = string.Join(" · ", new[] { context, view.Source, initiative }.Where(s => !string.IsNullOrEmpty(s)));
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
            var number = c.Delta is { } delta ? Points(delta) : c.WinRate is { } win ? win.ToString("P0", culture) : string.Empty;
            var lines = new List<string> { c.Name };
            if (c.WinRate is { } rate)
            {
                lines.Add(Loc.P("Mulligan.CardWinRate", c.Sample, rate.ToString("P1", culture), view.CardBaseline.ToString("P1", culture)));
                lines.Add(Loc.F("Mulligan.KeptBy", c.KeepRate?.ToString("P0", culture), c.ExpectedTurn.ToString("0.#", culture)));
            }
            else
            {
                lines.Add(Loc.T("Mulligan.NotEnoughGames"));
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
                number,
                c.Delta > 0.005,
                c.Delta < -0.005,
                synergy is not null,
                c.Copy > 1,
                late,
                kept,
                string.Join("\n", lines));
        }).ToList();
    }

    /// <summary>A win rate difference in points: 0.023 → "+2.3".</summary>
    private static string Points(double delta) => (delta * 100).ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);
}
