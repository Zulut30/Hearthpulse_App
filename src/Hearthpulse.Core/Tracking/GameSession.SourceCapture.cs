using Hearthpulse.Core.SourceCapture;

namespace Hearthpulse.Core.Tracking;

/// <summary>Source lifecycle seam for both composition roots. Disk/network ownership remains with the host.</summary>
public sealed partial class GameSession
{
    private readonly SourceCaptureRecorder _sourceCapture = new();

    /// <summary>Immutable observations on the parser thread; connect only a non-blocking spool callback.</summary>
    public event Action<SourceCaptureFrame>? SourceCaptureRecorded
    {
        add { lock (_pipelineGate) _sourceCapture.Recorded += value; }
        remove { lock (_pipelineGate) _sourceCapture.Recorded -= value; }
    }

    /// <summary>Apply after the spool's settings/notice gate has completed. No disk/network under the pipeline lock.</summary>
    public void SetSourceCaptureEnabled(bool enabled)
    {
        lock (_pipelineGate) _sourceCapture.SetEnabled(enabled);
    }

    /// <summary>Seal any ongoing capture before stopping/replacing the watcher, then await the spool's FlushAsync.</summary>
    public void StopSourceCapture()
    {
        lock (_pipelineGate)
        {
            CaptureUnpickedDraftOffer();
            _sourceCapture.Stop("shutdown");
        }
    }

    /// <summary>Known lost/rotated source from the host watcher. Arbitrary text is never copied into the source.</summary>
    public void SourceCaptureGap(string reason)
    {
        lock (_pipelineGate) _sourceCapture.NoteGap(reason);
    }
}
