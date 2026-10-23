using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App.Overlay;

/// <summary>The plate under one offered card: the guide's call, its numbers and whether the player keeps it.</summary>
/// <param name="Call">"Keep", "Mulligan" or "No data": what the guide recommends.</param>
/// <param name="Kept">The player's current choice (starts as the recommendation; a click toggles it).</param>
/// <param name="Note">Duplicate, curve or synergy remark; empty when there is none.</param>
public sealed record MulliganPlate(
    int EntityId,
    string Name,
    string Call,
    bool KeepCall,
    bool MulliganCall,
    string Delta,
    bool Good,
    bool Bad,
    string Numbers,
    string Note,
    bool Synergy,
    bool Kept,
    string Choice,
    string Tooltip);

/// <summary>The mulligan panel (MulliganPanel.xaml): one plate per offered card plus the impact of the chosen keeps.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showMulligan;
    private IReadOnlyList<MulliganPlate> _mulliganCards = [];
    private string _mulliganTitle = string.Empty;
    private string _mulliganNote = string.Empty;
    private string _mulliganImpact = string.Empty;
    private string _mulliganCompare = string.Empty;
    private bool _mulliganImpactGood;
    private bool _mulliganImpactBad;
    private IReadOnlyList<string> _mulliganWarnings = [];
    private string _mulliganSynergyNote = string.Empty;
    private MulliganView? _mulligan;
    private string? _mulliganHand;
    private string? _mulliganPrint;
    private readonly HashSet<int> _mulliganKept = new();
    private RelayCommand? _toggleMulliganCard;

    public bool ShowMulligan { get => _showMulligan; private set => Set(ref _showMulligan, value); }

    public string MulliganTitle { get => _mulliganTitle; private set => Set(ref _mulliganTitle, value); }

    public string MulliganNote { get => _mulliganNote; private set => Set(ref _mulliganNote, value); }

    public IReadOnlyList<MulliganPlate> MulliganCards { get => _mulliganCards; private set => Set(ref _mulliganCards, value); }

    /// <summary>"Your keeps (3 of 4): 53.4% expected".</summary>
    public string MulliganImpact { get => _mulliganImpact; private set => Set(ref _mulliganImpact, value); }

    /// <summary>The same estimate against a full mulligan and the recommended keeps.</summary>
    public string MulliganCompare { get => _mulliganCompare; private set => Set(ref _mulliganCompare, value); }

    public bool MulliganImpactGood { get => _mulliganImpactGood; private set => Set(ref _mulliganImpactGood, value); }

    public bool MulliganImpactBad { get => _mulliganImpactBad; private set => Set(ref _mulliganImpactBad, value); }

    public IReadOnlyList<string> MulliganWarnings { get => _mulliganWarnings; private set => Set(ref _mulliganWarnings, value); }

    /// <summary>Says when synergies cannot be shown (no pair data yet).</summary>
    public string MulliganSynergyNote { get => _mulliganSynergyNote; private set => Set(ref _mulliganSynergyNote, value); }

    /// <summary>Click a plate: mark the card as kept or replaced; the impact line follows.</summary>
    public RelayCommand ToggleMulliganCard => _toggleMulliganCard ??= new RelayCommand(p =>
    {
        if (p is int id && !_mulliganKept.Remove(id))
        {
            _mulliganKept.Add(id);
        }

        RebuildMulligan();
    });

    private void UpdateMulligan(MulliganView? mulligan, AppSettings settings)
    {
        ShowMulligan = settings.ShowMulligan && mulligan is not null && mulligan.Cards.Any(c => c.KeepRate is not null);
        _mulligan = mulligan;
        if (mulligan is null)
        {
            _mulliganHand = null;
            _mulliganPrint = null;
            return;
        }

        // A new hand (or a new source for it) starts from the recommendation; the player's clicks last until then.
        var hand = mulligan.Context + "|" + string.Join(",", mulligan.Cards.Select(c => c.EntityId + c.CardId));
        if (hand != _mulliganHand)
        {
            _mulliganHand = hand;
            _mulliganKept.Clear();
            _mulliganKept.UnionWith(mulligan.Recommended);
        }

        RebuildMulligan();
    }

    private void RebuildMulligan()
    {
        if (_mulligan is not { } view)
        {
            return;
        }

        // Snapshots arrive every 100 ms while anything changes; rebuild the plates only when what they show changed.
        var print = $"{view.Context}|{view.Source}|{view.BaseWinRate:0.0000}|{view.ReplaceDelta:0.0000}|{string.Join(",", _mulliganKept.Order())}|"
                    + string.Join(";", view.Cards.Select(c => $"{c.EntityId}{c.CardId}{c.Call}{c.Delta:0.0000}{c.KeepRate:0.0000}"));
        if (print == _mulliganPrint)
        {
            return;
        }

        _mulliganPrint = print;
        var culture = CultureInfo.CurrentCulture;
        var impact = MulliganGuide.Evaluate(view, _mulliganKept);
        var vsClass = view.OpponentClass is { } opponent ? $" vs {ClassName(opponent)}" : string.Empty;
        var initiative = view.GoingFirst switch { true => " going first", false => " on the coin", null => string.Empty };
        MulliganTitle = $"Mulligan · {view.Context}";
        MulliganNote = $"Average hand{vsClass}{initiative}: {view.BaseWinRate.ToString("P1", culture)} · {view.Source}";

        var vsFull = impact.Expected - impact.FullMulligan;
        MulliganImpact = $"Your keeps ({impact.Kept} of {view.Cards.Count}): {impact.Expected.ToString("P1", culture)} expected";
        MulliganCompare = $"{Points(vsFull)} vs full mulligan · recommended keeps {impact.Recommended.ToString("P1", culture)}";
        MulliganImpactGood = impact.Expected >= impact.Recommended - 0.0005 && vsFull > 0.0005;
        MulliganImpactBad = impact.Expected < impact.Recommended - 0.005;
        MulliganWarnings = impact.Warnings
            .Concat(impact.Synergies.Select(s => $"Synergy kept: {s.NameA} + {s.NameB} ({Points(s.Interaction)})"))
            .ToList();
        MulliganSynergyNote = view.HasPairData ? string.Empty : "Synergies: the server has no card-pair data yet, so none are shown.";

        MulliganCards = view.Cards.Select(c =>
        {
            var kept = _mulliganKept.Contains(c.EntityId);
            var synergy = view.Synergies.FirstOrDefault(s => s.EntityA == c.EntityId || s.EntityB == c.EntityId);
            var note = c.Copy > 1 ? "2nd copy: counts once (heuristic)"
                : synergy is not null ? $"Synergy with {(synergy.EntityA == c.EntityId ? synergy.NameB : synergy.NameA)} ({Points(synergy.Interaction)})"
                : MulliganGuide.IsLate(c) ? $"Late card: usually played turn {c.ExpectedTurn.ToString("0", culture)}"
                : string.Empty;
            var numbers = string.Join(" · ", new[]
            {
                c.WinRate is { } win ? $"WR {win.ToString("P1", culture)}" : null,
                c.KeepRate is { } keep ? $"kept {keep.ToString("P0", culture)}" : null,
            }.OfType<string>());
            var tooltip = c.WinRate is { } rate
                ? $"{c.Name}: win rate {rate.ToString("P1", culture)} with the card in the opening hand, against {(view.BaseWinRate - (view.ClassShift ?? 0)).ToString("P1", culture)} for an average hand ({c.Sample.ToString("N0", culture)} offers). "
                  + $"Kept by {c.KeepRate?.ToString("P0", culture)} of players; usually played on turn {c.ExpectedTurn.ToString("0.#", culture)}. Click to mark it kept or replaced."
                : $"{c.Name}: not enough games for numbers. Click to mark it kept or replaced.";
            return new MulliganPlate(
                c.EntityId,
                c.Name,
                c.Call switch { Core.Tracking.MulliganCall.Keep => "Keep", Core.Tracking.MulliganCall.Mulligan => "Mulligan", _ => "No data" },
                c.Call == Core.Tracking.MulliganCall.Keep,
                c.Call == Core.Tracking.MulliganCall.Mulligan,
                c.Delta is { } delta ? Points(delta) : string.Empty,
                c.Delta > 0.005,
                c.Delta < -0.005,
                numbers,
                note,
                synergy is not null,
                kept,
                kept ? "Keeping" : "Replacing",
                tooltip);
        }).ToList();
    }

    /// <summary>A win rate difference in points: 0.023 → "+2.3".</summary>
    private static string Points(double delta) => (delta * 100).ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);
}
