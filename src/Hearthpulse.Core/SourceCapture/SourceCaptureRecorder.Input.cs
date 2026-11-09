using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.SourceCapture;

public sealed partial class SourceCaptureRecorder
{
    private int _pendingInputGaps;

    /// <summary>
    /// Classifies unsupported grammar only. Raw text is never retained, sanitized or serialized; supported facts
    /// still come exclusively from IPowerListener. PowerTaskList display replay is intentionally outside v1.
    /// </summary>
    internal void ObservePowerLine(string line)
    {
        if (!_enabled || _catchingUp || _stopped || _game is null) return;
        var supported = false;
        if (PowerLine.TrySplit(line, out _, out var methodRange, out var bodyRange))
        {
            var method = line.AsSpan(methodRange);
            var body = line[bodyRange].Trim();
            supported = method switch
            {
                "DebugPrintPower" => body is "CREATE_GAME" or "BLOCK_END" || body.StartsWith("GameEntity EntityID=", StringComparison.Ordinal)
                    || body.StartsWith("Player EntityID=", StringComparison.Ordinal) || body.StartsWith("FULL_ENTITY - ", StringComparison.Ordinal)
                    || body.StartsWith("SHOW_ENTITY - ", StringComparison.Ordinal) || body.StartsWith("CHANGE_ENTITY - ", StringComparison.Ordinal)
                    || body.StartsWith("HIDE_ENTITY - ", StringComparison.Ordinal) || body.StartsWith("BLOCK_START ", StringComparison.Ordinal)
                    || body.StartsWith("TAG_CHANGE ", StringComparison.Ordinal) && PowerLine.TryTagChange(body, out _, out _, out _)
                    || body.StartsWith("tag=", StringComparison.Ordinal) && PowerLine.TryTagValue(body, out _, out _),
                "DebugPrintGame" => true,
                "DebugPrintEntityChoices" or "DebugPrintEntitiesChosen" => body.StartsWith("id=", StringComparison.Ordinal)
                    || body.StartsWith("Source=", StringComparison.Ordinal) || body.StartsWith("Entities[", StringComparison.Ordinal),
                _ => false,
            };
        }
        else if (!line.Contains("GameState.", StringComparison.Ordinal)) return;
        if (supported) return;
        if (_capture is null) _pendingInputGaps++;
        else NoteGap("unsupported");
    }
}
