namespace Hearthpulse.Core;

/// <summary>Settings of the sharing features (session recap, deck picture, YouTube timestamps, the OBS "Session" window).</summary>
public sealed partial class AppSettings
{
    /// <summary>The OBS capture window "Hearthpulse — Session" (rank or rating since the stream started) is open.</summary>
    public bool StreamSessionWindow { get; set; }

    /// <summary>When the player last pressed "Start a new session" (local time); null: the session follows the ladder session's rule.</summary>
    public DateTime? StreamSessionStart { get; set; }

    /// <summary>Share pictures show the player's BattleTag (off by default: no name on a picture unless asked).</summary>
    public bool ShareShowBattleTag { get; set; }

    /// <summary>YouTube timestamps: the recording starts this many seconds before the first game unless set by hand.</summary>
    public int TimestampLeadInSeconds { get; set; } = Share.YouTubeTimestamps.DefaultLeadInSeconds;

    private void NormalizeGrowth()
    {
        TimestampLeadInSeconds = Math.Clamp(TimestampLeadInSeconds, 0, Share.YouTubeTimestamps.MaxLeadInSeconds);
        if (StreamSessionStart is { } start && (start.Year < 2000 || start > DateTime.Now.AddDays(1)))
        {
            StreamSessionStart = null;
        }
    }
}
