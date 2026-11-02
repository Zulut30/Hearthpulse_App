using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>The recorder's own bookkeeping: an entity in view, an open choice, a hero attack, a step.</summary>
public sealed partial class BattlegroundsReplayRecorder
{
    private sealed class Shown(int id)
    {
        public int Id { get; } = id;

        public char Side { get; set; }

        public string Zone { get; set; } = string.Empty;

        public int Position { get; set; }

        public string Card { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;
    }

    /// <summary>Our choice offered and not picked yet: its option entities, read again at the pick.</summary>
    private sealed record OpenChoice(string Type, Entity? Source, IReadOnlyList<Entity> Options);

    /// <summary>A hero attacking after the fight: the defender's health + armor before it.</summary>
    private sealed record HeroAttack(bool Ours, Entity? Defender, int Before, int Depth);

    /// <summary>An open top-level block that is a step; a lazy one is written only if it changed what is shown.</summary>
    private sealed class Step(string kind, Entity? source, bool lazy)
    {
        public string Kind { get; } = kind;

        public Entity? Source { get; } = source;

        public bool Lazy { get; } = lazy;

        /// <summary>The attack's defender (game entity id), once known.</summary>
        public int Target { get; set; }

        /// <summary>The parser's block depth the step's block started at: it ends when the depth is back there.</summary>
        public int Depth { get; set; }
    }
}
