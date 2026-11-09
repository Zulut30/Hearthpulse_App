using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture.Processing;

/// <summary>Public context of the capture as written in its start record; v1 sources have no format or Arena kind.</summary>
public sealed record SourceCaptureContext(string Mode, int Day, int? Build, string? Format, string? Arena);

/// <summary>
/// Per-metric fitness (ADR 0013). Reasons come from a fixed list, in its order, without repeats; the global
/// CompleteForAggregation flag is unchanged and separate.
/// </summary>
public sealed record SourceCaptureEligibility(bool Outcome, bool Offers, IReadOnlyList<string> Reasons);

/// <summary>Facts the eligibility admits: null outcome and no offers when the matching flag is false.</summary>
public sealed record SourceCaptureFacts(SourceCaptureOutcome? Outcome, IReadOnlyList<SourceCaptureOffer> Offers);

/// <summary>The local player's own visible result: W/L/T, or the place in Battlegrounds.</summary>
public sealed record SourceCaptureOutcome(string? Result, int? Placement);

/// <summary>One visible offer at the moment of the pick; Seq is the source record that resolved it.</summary>
public sealed record SourceCaptureOffer(int Seq, string Kind, IReadOnlyList<string> Options, IReadOnlyList<string> Picked);

/// <summary>The last record of one choice id in a source.</summary>
internal sealed record SourceCaptureChoice(int Seq, string? ChoiceType, string[] Options, string[] Picked, bool Resolved);

/// <summary>What the decoder state knows at the end of a source; FirstChoice is the lowest choice id.</summary>
internal sealed record SourceCaptureFinish(int Version, string CaptureKind, string Mode, bool CompleteForAggregation,
    JsonElement? End, bool LifecycleGap, SourceCaptureChoice? FirstChoice);

/// <summary>Contract C2: the eligibility rules of rulesVersion bg-hero-1 and the facts they admit.</summary>
internal static class SourceCaptureDerivation
{
    /// <summary>The fixed order of reasons in the result.</summary>
    internal static readonly string[] ReasonOrder =
        ["source_v1", "draft", "no_end", "end_reason", "lifecycle_gap", "no_outcome", "no_offer", "offer_unresolved", "offer_shape"];

    internal static (SourceCaptureEligibility Eligibility, SourceCaptureFacts Facts) Derive(SourceCaptureFinish s)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        bool bg = s.Mode is "bg" or "duos";
        bool outcome, offers;
        SourceCaptureOutcome? outcomeFact = null;
        SourceCaptureOffer? offer = null;
        if (s.Version == 1)
        {
            // v1 cannot tell the structural BG gap from a real one, so a BG v1 game is honestly unfit.
            reasons.Add("source_v1");
            if (s.CaptureKind == "draft") reasons.Add("draft");
            outcome = s.CaptureKind == "game" && !bg && s.CompleteForAggregation;
            offers = false;
            if (outcome) outcomeFact = Outcome(s.End!.Value);
        }
        else if (s.CaptureKind == "draft")
        {
            reasons.Add("draft");
            outcome = offers = false;
        }
        else
        {
            bool lifecycle = Lifecycle(s, reasons);
            var place = s.End is { } end ? Outcome(end) : null;
            bool hasOutcome = place is not null && s.Mode switch
            {
                "bg" => place.Placement is >= 1 and <= 8,
                "duos" => place.Placement is >= 1 and <= 4,
                _ => place.Result is "W" or "L" or "T",
            };
            if (s.End is not null && !hasOutcome) reasons.Add("no_outcome");
            outcome = lifecycle && hasOutcome;
            if (outcome) outcomeFact = place;
            offer = bg ? HeroOffer(s.FirstChoice, reasons) : null;
            if (!bg) reasons.Add("no_offer");
            offers = lifecycle && offer is not null;
        }
        var ordered = ReasonOrder.Where(reasons.Contains).ToArray();
        return (new(outcome, offers, ordered), new(outcomeFact, offers ? [offer!] : []));
    }

    // end.reason == normal and no late_attach/rotation/shutdown gap; other gaps do not spoil the outcome.
    private static bool Lifecycle(SourceCaptureFinish s, HashSet<string> reasons)
    {
        bool ok = true;
        if (s.End is not { } end) { reasons.Add("no_end"); ok = false; }
        else if (end.GetProperty("reason").GetString() != "normal") { reasons.Add("end_reason"); ok = false; }
        if (s.LifecycleGap) { reasons.Add("lifecycle_gap"); ok = false; }
        return ok;
    }

    private static SourceCaptureOutcome Outcome(JsonElement end) => new(
        end.GetProperty("result").ValueKind == JsonValueKind.Null ? null : end.GetProperty("result").GetString(),
        end.GetProperty("placement").ValueKind == JsonValueKind.Null ? null : end.GetProperty("placement").GetInt32());

    // The hero pick is the first choice of the game, typed MULLIGAN, resolved, 2-4 visible heroes and one pick.
    private static SourceCaptureOffer? HeroOffer(SourceCaptureChoice? first, HashSet<string> reasons)
    {
        if (first is null || first.ChoiceType != "mulligan") { reasons.Add("no_offer"); return null; }
        if (!first.Resolved) { reasons.Add("offer_unresolved"); return null; }
        if (first.Options.Length is < 2 or > 4 || first.Picked.Length != 1) { reasons.Add("offer_shape"); return null; }
        return new(first.Seq, "bg_hero", first.Options, first.Picked);
    }
}
