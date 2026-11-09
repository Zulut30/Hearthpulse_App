using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture.Processing;

/// <summary>Hard ceilings may be lowered by an operator, never raised above the v1 worker budget.</summary>
public sealed record SourceCaptureLimits(
    long MaxSourceBytes = 256L * 1024 * 1024,
    int MaxLineBytes = 64 * 1024,
    int MaxRecords = 100_000,
    long MaxDerivedBytes = 64L * 1024 * 1024);

/// <summary>A validated observation; references are capture-scoped, never original game/account IDs.</summary>
public sealed record SourceCaptureEvent(int Seq, string Kind, int Turn, string Side, int Entity,
    string? Card, int Target, JsonElement Data);

/// <summary>Observed counts only. Turns is the highest observed turn, not proof of every intervening turn.</summary>
public sealed record SourceCaptureSummary(int Plays, int Buys, int Sells, int Choices, int Turns,
    int ShopSnapshots, int Resources, int DraftChoices, string CaptureKind, string Mode,
    string CaptureStatus, bool HasEnd, bool CompleteForAggregation, string? Result, int? Placement);

/// <summary>One replaceable derived contribution, identified by exact source bytes and parser version.</summary>
public sealed record SourceCaptureResult(int V, string ParserVersion, string SourceSha256, string Status,
    int Records, int Unsupported, int Gaps, IReadOnlyList<SourceCaptureEvent> Events, SourceCaptureSummary Summary);

/// <summary>A safe machine error; neither its message nor its code contains input contents.</summary>
public sealed class SourceCaptureException : Exception
{
    internal SourceCaptureException(string code) : base(code) => Code = code;

    /// <summary>Fixed technical discriminator suitable for a private worker's status channel.</summary>
    public string Code { get; }
}
