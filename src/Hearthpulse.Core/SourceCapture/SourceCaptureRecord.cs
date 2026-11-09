using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture;

/// <summary>A validated observation. Construction stays inside the visible projection, never a raw-log API.</summary>
public sealed class SourceCaptureRecord
{
    internal SourceCaptureRecord(int seq, string kind, int turn, string side, int entity, string? card, int target, object data)
    {
        Seq = seq; Kind = kind; Turn = Math.Clamp(turn, 0, 1000); Side = side;
        Entity = entity; Card = card; Target = target; Data = data;
    }

    public int V => 1;
    public int Seq { get; }
    public string Kind { get; }
    public int Turn { get; }
    public string Side { get; }
    public int Entity { get; }
    public string? Card { get; }
    public int Target { get; }
    public object Data { get; }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Compact source v1; the spool adds the mandatory LF.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Metadata and one observation passed to a background spool; no source game/account identifiers.</summary>
public sealed record SourceCaptureFrame(string CaptureId, string CaptureKind, string Mode, int Day, SourceCaptureRecord Record);

/// <summary>A transport-neutral technical state, suitable for presentation without exposing source contents.</summary>
public sealed record SourceCaptureState(string Status, long Dropped, int PendingCaptures, int PendingDeletions);

internal static class SourceCaptureTokens
{
    internal static bool Card(string? value) => value is { Length: > 0 and <= 80 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    internal static bool Hex(string? value, int length) => value?.Length == length
        && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
