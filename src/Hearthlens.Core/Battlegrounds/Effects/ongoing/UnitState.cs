using System.Runtime.CompilerServices;

namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Per-minion state that does not fit in <see cref="Unit.Counter"/>/<see cref="Unit.Counter2"/> (a list, a reference to
/// another minion, or a counter on a minion whose own card already uses them). Effects are shared singletons, so the
/// state lives here, keyed by the <see cref="Unit"/> of one fight: it is never shared between fights, and it is freed
/// with the unit.
/// </summary>
internal static class UnitState<T> where T : class, new()
{
    private static readonly ConditionalWeakTable<Unit, T> Table = new();

    public static T Of(Unit unit) => Table.GetOrCreateValue(unit);
}
