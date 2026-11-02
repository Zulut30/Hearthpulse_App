using System.Globalization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>The tracker's own records: a captured combat, a board seen, a hero hit.</summary>
public sealed partial class BattlegroundsTracker
{
    internal sealed record CombatCapture(SimSide Ours, SimSide Theirs, bool? OursFirst, int FirstAttackerEntityId);

    private sealed record SeenBoard(int Turn, IReadOnlyList<BgMinion> Minions);

    /// <summary>A hero attack of the fight: whose hero hit, the hero it hit and how sturdy that was, the block depth.</summary>
    private sealed record HeroHit(bool Ours, Entity? Defender, int Before, int Depth);
}
