namespace Hearthpulse.Core.Replay;

// Stub of P2: P1's file of the same path replaces it at the merge (same signatures).
public sealed partial class MatchPage
{
    /// <summary>True when the game's file holds the Battlegrounds step-by-step replay (format 4).</summary>
    public bool HasBattlegroundsSteps => false;

    /// <summary>The Battlegrounds step at <paramref name="frame"/>, or null without one.</summary>
    public MatchBgStepView? BattlegroundsStep(int frame) => null;

    private IReadOnlyList<MatchStepMark> BattlegroundsMarks() => [];

    private IReadOnlyList<MatchTimelineSpan> BattlegroundsSpans() => [];
}
