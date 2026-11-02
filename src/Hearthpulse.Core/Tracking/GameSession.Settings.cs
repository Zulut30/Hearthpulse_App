namespace Hearthpulse.Core.Tracking;

/// <summary>The settings that steer the live session, the same for both apps.</summary>
public sealed partial class GameSession
{
    /// <summary>
    /// Applies the settings the session follows: at start and after every settings change, on the UI thread.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        // No game plan panel: no comp to choose or see, so the advisor plans around none (a comp saved by an older
        // version stays in the settings, unused).
        SetCompMode(CompMode.Manual, null);
        SetCardPoolEnabled(settings.ShowCardPool);
        SetArenaDraftEnabled(settings.ShowArenaDraft);
        SetBgOpponentMmrEnabled(settings.ShowBgOpponentMmr);
        SetLadderBreak(TimeSpan.FromHours(Math.Clamp(settings.LadderSessionBreakHours, 1, 12)));
    }

    /// <summary>What <see cref="ApplySettings"/> set, for tests.</summary>
    internal (bool CardPool, bool ArenaDraft, bool BgOpponentMmr, TimeSpan LadderBreak) Steering =>
        (_cardPoolEnabled, _arenaDraftEnabled, _bgMmrEnabled, _ladderBreak);
}
